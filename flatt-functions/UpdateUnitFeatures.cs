using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System;
using System.IO;

namespace flatt_functions
{
    public class UpdateUnitFeatures
    {
        private readonly ILogger<UpdateUnitFeatures> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public UpdateUnitFeatures(ILogger<UpdateUnitFeatures> logger, IConfiguration configuration)
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

        private record UpdateUnitFeaturesRequest(
            [property: JsonPropertyName("featureIds")] List<int>? FeatureIds
        );

        [Function("UpdateUnitFeaturesForUnit")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "units/{id:int}/features")] HttpRequestData req,
            string id)
        {
            var response = req.CreateResponse();
            AddCors(response);

            try
            {
                _logger.LogInformation("🔧 UpdateUnitFeaturesForUnit started for UnitID {id}", id);

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
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = JsonNumberHandling.AllowReadingFromString
                };

                List<int>? featureIds = null;

                try
                {
                    var model = JsonSerializer.Deserialize<UpdateUnitFeaturesRequest>(body, options);
                    featureIds = model?.FeatureIds;
                }
                catch
                {
                    // ignore and try array parse next
                }

                if (featureIds == null)
                {
                    try
                    {
                        featureIds = JsonSerializer.Deserialize<List<int>>(body, options);
                    }
                    catch
                    {
                        // still null
                    }
                }

                if (featureIds == null)
                {
                    return await ErrorResponse(req, HttpStatusCode.BadRequest, new
                    {
                        Error = true,
                        Message = "Invalid request format. Provide featureIds: number[] or raw array [1,2,3].",
                        StatusCode = 400
                    });
                }

                // Normalize: dedupe, filter invalid
                var cleaned = featureIds
                    .Where(f => f > 0)
                    .Distinct()
                    .ToList();

                // Optional: allow empty list to mean clear all features
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();

                // Ensure unit exists (avoid orphan rows)
                if (!await CheckUnitExists(connection, unitId))
                {
                    return await ErrorResponse(req, HttpStatusCode.NotFound, new
                    {
                        Error = true,
                        Message = $"Vehicle with UnitID {unitId} not found",
                        StatusCode = 404
                    });
                }

                using var tran = connection.BeginTransaction();
                int deletedCount = 0;
                int insertedCount = 0;

                try
                {
                    // Delete existing
                    using (var deleteCmd = new SqlCommand("DELETE FROM [dbo].[UnitFeatures] WHERE [UnitID] = @UnitID", connection, tran))
                    {
                        deleteCmd.Parameters.AddWithValue("@UnitID", unitId);
                        deletedCount = await deleteCmd.ExecuteNonQueryAsync();
                    }

                    if (cleaned.Count > 0)
                    {
                        // Build multi-row insert
                        var values = new List<string>();
                        var cmd = new SqlCommand();
                        cmd.Connection = connection;
                        cmd.Transaction = tran;

                        cmd.CommandText = "INSERT INTO [dbo].[UnitFeatures] ([UnitID], [FeatureID]) VALUES ";
                        cmd.Parameters.AddWithValue("@UnitID", unitId);

                        for (int i = 0; i < cleaned.Count; i++)
                        {
                            string paramName = "@F" + i;
                            values.Add($"(@UnitID, {paramName})");
                            cmd.Parameters.AddWithValue(paramName, cleaned[i]);
                        }

                        cmd.CommandText += string.Join(", ", values);
                        insertedCount = await cmd.ExecuteNonQueryAsync();
                    }

                    await tran.CommitAsync();
                }
                catch (Exception ex)
                {
                    await tran.RollbackAsync();
                    _logger.LogError(ex, "❌ UpdateUnitFeaturesForUnit failed: {message}", ex.Message);
                    return await ErrorResponse(req, HttpStatusCode.InternalServerError, new
                    {
                        Error = true,
                        Message = "An internal server error occurred while updating unit features",
                        Details = ex.Message
                    });
                }

                // Success
                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await response.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    Success = true,
                    Message = "Unit features updated successfully",
                    UnitId = unitId,
                    DeletedCount = deletedCount,
                    InsertedCount = insertedCount,
                    FeatureIds = cleaned
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

                _logger.LogInformation("✅ Unit features updated for UnitID {unitId} - deleted {deletedCount}, inserted {insertedCount}", unitId, deletedCount, insertedCount);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Unexpected error in UpdateUnitFeaturesForUnit: {message}", ex.Message);
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
            response.Headers.Add("Access-Control-Allow-Methods", "GET, PUT, POST, OPTIONS");
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
