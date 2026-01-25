using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace flatt_functions
{
    public class AddFeatureToUnit
    {
        private readonly ILogger<AddFeatureToUnit> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public AddFeatureToUnit(ILogger<AddFeatureToUnit> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;

            var connectionString = configuration["SqlConnectionString"] ??
                                     configuration.GetConnectionString("SqlConnectionString") ??
                                     configuration["ConnectionStrings:SqlConnectionString"];

            if (string.IsNullOrEmpty(connectionString))
            {
                _logger.LogError("SqlConnectionString is null or empty in configuration");
                throw new InvalidOperationException("SqlConnectionString not set in configuration.");
            }

            _connectionString = connectionString;
        }

        private record AddFeatureRequest(
            [property: JsonPropertyName("featureName")] string? FeatureName,
            [property: JsonPropertyName("category")] string? Category,
            [property: JsonPropertyName("isActive")] bool? IsActive,
            [property: JsonPropertyName("description")] string? Description
        );

        [Function("AddFeatureToUnit")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "units/{id:int}/features/add")] HttpRequestData req,
            string id)
        {
            var response = req.CreateResponse();
            AddCors(response);

            try
            {
                _logger.LogInformation("🔧 AddFeatureToUnit started for UnitID {id}", id);

                // Validate UnitID
                if (!int.TryParse(id, out var unitId) || unitId <= 0)
                {
                    return await ErrorResponse(req, HttpStatusCode.BadRequest, new
                    {
                        Error = true,
                        Message = "Invalid UnitID format. Must be a positive number.",
                        StatusCode = 400
                    });
                }

                // Parse request body
                string body;
                using (var reader = new StreamReader(req.Body))
                {
                    body = await reader.ReadToEndAsync();
                }

                if (string.IsNullOrWhiteSpace(body))
                {
                    return await ErrorResponse(req, HttpStatusCode.BadRequest, new
                    {
                        Error = true,
                        Message = "Request body is required",
                        StatusCode = 400
                    });
                }

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var featureRequest = JsonSerializer.Deserialize<AddFeatureRequest>(body, options);

                if (featureRequest == null || string.IsNullOrWhiteSpace(featureRequest.FeatureName))
                {
                    return await ErrorResponse(req, HttpStatusCode.BadRequest, new
                    {
                        Error = true,
                        Message = "featureName is required",
                        StatusCode = 400
                    });
                }

                // Truncate feature name to 50 characters to fit database column limit
                var originalFeatureName = featureRequest.FeatureName.Trim();
                var featureName = originalFeatureName.Length > 50 
                    ? originalFeatureName.Substring(0, 50) 
                    : originalFeatureName;
                
                if (originalFeatureName.Length > 50)
                {
                    _logger.LogWarning("⚠️ Feature name truncated from {original} to {truncated}", originalFeatureName, featureName);
                }

                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                // Check if unit exists
                if (!await CheckUnitExists(connection, unitId))
                {
                    return await ErrorResponse(req, HttpStatusCode.NotFound, new
                    {
                        Error = true,
                        Message = $"Unit with ID {unitId} not found",
                        StatusCode = 404
                    });
                }

                // Step 1: Check if feature exists in FeatureList
                int featureId;
                bool featureExisted = false;
                
                // First, let's check the actual column names in FeatureList
                var checkFeatureQuery = @"
                    SELECT TOP 1 * 
                    FROM [dbo].[FeatureList] 
                    WHERE LOWER(TRIM([FeatureName])) = LOWER(TRIM(@FeatureName))";

                using (var checkCmd = new SqlCommand(checkFeatureQuery, connection))
                {
                    checkCmd.Parameters.AddWithValue("@FeatureName", featureName);
                    
                    using var reader = await checkCmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        // Feature exists, use existing FeatureID
                        featureId = reader.GetInt32(reader.GetOrdinal("FeatureID"));
                        featureExisted = true;
                        _logger.LogInformation("✅ Feature '{featureName}' already exists with ID {featureId}", featureName, featureId);
                    }
                    else
                    {
                        // Close reader before insert
                        await reader.CloseAsync();
                        
                        // Feature doesn't exist, create it - use only FeatureName since other columns may not exist
                        var insertFeatureQuery = @"
                            INSERT INTO [dbo].[FeatureList] ([FeatureName])
                            OUTPUT INSERTED.FeatureID
                            VALUES (@FeatureName)";

                        using var insertCmd = new SqlCommand(insertFeatureQuery, connection);
                        insertCmd.Parameters.AddWithValue("@FeatureName", featureName);

                        var insertResult = await insertCmd.ExecuteScalarAsync();
                        featureId = Convert.ToInt32(insertResult!);
                        _logger.LogInformation("✨ Created new feature '{featureName}' with ID {featureId}", featureName, featureId);
                    }
                }

                // Step 2: Check if this unit already has this feature
                var checkMappingQuery = @"
                    SELECT COUNT(1) 
                    FROM [dbo].[UnitFeatures] 
                    WHERE [UnitID] = @UnitID AND [FeatureID] = @FeatureID";

                bool mappingExists = false;
                using (var checkMappingCmd = new SqlCommand(checkMappingQuery, connection))
                {
                    checkMappingCmd.Parameters.AddWithValue("@UnitID", unitId);
                    checkMappingCmd.Parameters.AddWithValue("@FeatureID", featureId);
                    var count = (int)await checkMappingCmd.ExecuteScalarAsync()!;
                    mappingExists = count > 0;
                }

                // Step 3: Add feature to unit if not already assigned
                if (!mappingExists)
                {
                    var insertMappingQuery = @"
                        INSERT INTO [dbo].[UnitFeatures] ([UnitID], [FeatureID])
                        VALUES (@UnitID, @FeatureID)";

                    using var insertMappingCmd = new SqlCommand(insertMappingQuery, connection);
                    insertMappingCmd.Parameters.AddWithValue("@UnitID", unitId);
                    insertMappingCmd.Parameters.AddWithValue("@FeatureID", featureId);
                    await insertMappingCmd.ExecuteNonQueryAsync();
                    
                    _logger.LogInformation("✅ Added feature {featureId} to unit {unitId}", featureId, unitId);
                }
                else
                {
                    _logger.LogInformation("ℹ️ Feature {featureId} already assigned to unit {unitId}", featureId, unitId);
                }

                // Success response
                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await response.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    Success = true,
                    Message = mappingExists 
                        ? "Feature already assigned to unit" 
                        : "Feature successfully added to unit",
                    UnitId = unitId,
                    FeatureId = featureId,
                    FeatureName = featureName,
                    OriginalName = originalFeatureName != featureName ? originalFeatureName : null,
                    Truncated = originalFeatureName.Length > 50,
                    FeatureExisted = featureExisted,
                    MappingExisted = mappingExists,
                    AlreadyAssigned = mappingExists,
                    NewlyCreated = !featureExisted,
                    Timestamp = DateTime.UtcNow
                }, new JsonSerializerOptions 
                { 
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase, 
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                }));

                return response;
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex, "❌ SQL error in AddFeatureToUnit: {message}", ex.Message);
                return await ErrorResponse(req, HttpStatusCode.InternalServerError, new
                {
                    Error = true,
                    Message = "Database error occurred while adding feature",
                    Details = ex.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Unexpected error in AddFeatureToUnit: {message}", ex.Message);
                return await ErrorResponse(req, HttpStatusCode.InternalServerError, new
                {
                    Error = true,
                    Message = "An unexpected error occurred",
                    Details = ex.Message
                });
            }
        }

        private static void AddCors(HttpResponseData response)
        {
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        }

        private static async Task<HttpResponseData> ErrorResponse(HttpRequestData req, HttpStatusCode status, object payload)
        {
            var res = req.CreateResponse(status);
            AddCors(res);
            res.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await res.WriteStringAsync(JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
            return res;
        }

        private static async Task<bool> CheckUnitExists(SqlConnection connection, int unitId)
        {
            using var cmd = new SqlCommand("SELECT 1 FROM [dbo].[Units] WHERE [UnitID] = @UnitID", connection);
            cmd.Parameters.AddWithValue("@UnitID", unitId);
            var result = await cmd.ExecuteScalarAsync();
            return result != null;
        }
    }
}
