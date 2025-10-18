using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using System.Linq;
using System;
using Microsoft.Data.SqlClient;
using System.Diagnostics;

namespace flatt_functions
{
    public class CheckStatus
    {
        private readonly ILogger<CheckStatus> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public CheckStatus(ILogger<CheckStatus> logger, IConfiguration configuration)
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

        [Function("CheckStatus")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checkstatus/{id}")] HttpRequestData req,
            string id)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = req.CreateResponse();
            
            try
            {
                _logger.LogInformation("🔍 CheckStatus function started - UnitID: {unitId}", id);
                
                // Add CORS headers
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                
                // Validate ID parameter
                if (!int.TryParse(id, out int unitId))
                {
                    _logger.LogWarning("⚠️ Invalid UnitID format: {id}", id);
                    response.StatusCode = HttpStatusCode.BadRequest;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    
                    var errorResponse = new
                    {
                        Error = true,
                        Message = "UnitID must be a valid number",
                        ProvidedId = id,
                        StatusCode = 400
                    };
                    
                    await response.WriteStringAsync(JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true
                    }));
                    
                    return response;
                }
                
                // Get status from database
                var status = await GetUnitStatus(unitId);
                
                stopwatch.Stop();
                
                if (status == null)
                {
                    _logger.LogWarning("⚠️ Unit not found - UnitID: {unitId}", unitId);
                    response.StatusCode = HttpStatusCode.NotFound;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    
                    var notFoundResponse = new
                    {
                        Error = true,
                        Message = $"Unit with ID {unitId} not found",
                        UnitId = unitId,
                        StatusCode = 404
                    };
                    
                    await response.WriteStringAsync(JsonSerializer.Serialize(notFoundResponse, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true
                    }));
                    
                    return response;
                }
                
                _logger.LogInformation("✅ Status retrieved successfully - UnitID: {unitId}, Status: {status}", unitId, status);
                
                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                
                var successResponse = new
                {
                    UnitId = unitId,
                    Status = status,
                    ResponseTimeMs = stopwatch.ElapsedMilliseconds,
                    Timestamp = DateTime.UtcNow
                };
                
                await response.WriteStringAsync(JsonSerializer.Serialize(successResponse, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                }));
                
                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "❌ Error in CheckStatus function - {errorType}: {message}", 
                    ex.GetType().Name, ex.Message);
                
                response.StatusCode = HttpStatusCode.InternalServerError;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                
                var errorResponse = new
                {
                    Error = true,
                    Message = "An internal server error occurred",
                    Details = ex.Message,
                    StatusCode = 500
                };
                
                await response.WriteStringAsync(JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                }));
                
                return response;
            }
        }

        private async Task<string?> GetUnitStatus(int unitId)
        {
            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                
                var query = "SELECT [Status] FROM [Units] WHERE [UnitID] = @UnitID";
                
                using var command = new SqlCommand(query, connection);
                command.Parameters.AddWithValue("@UnitID", unitId);
                command.CommandTimeout = 10;
                
                var result = await command.ExecuteScalarAsync();
                
                return result?.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to retrieve status for UnitID: {unitId}", unitId);
                throw;
            }
        }
    }
}
