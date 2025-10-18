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
    public class CheckVin
    {
        private readonly ILogger<CheckVin> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public CheckVin(ILogger<CheckVin> logger, IConfiguration configuration)
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

        [Function("CheckVin")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checkvin/{vin}")] HttpRequestData req,
            string vin)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = req.CreateResponse();
            
            try
            {
                _logger.LogInformation("🔍 CheckVin function started - VIN: {vin}", vin);
                
                // Add CORS headers
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                
                // Validate VIN parameter
                if (string.IsNullOrWhiteSpace(vin))
                {
                    _logger.LogWarning("⚠️ Empty VIN provided");
                    response.StatusCode = HttpStatusCode.BadRequest;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    
                    var errorResponse = new
                    {
                        Error = true,
                        Message = "VIN is required",
                        StatusCode = 400
                    };
                    
                    await response.WriteStringAsync(JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true
                    }));
                    
                    return response;
                }
                
                // Check if VIN exists
                var vinCheck = await CheckVinExists(vin);
                
                stopwatch.Stop();
                
                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                
                var successResponse = new
                {
                    Vin = vin,
                    Exists = vinCheck.Exists,
                    Message = vinCheck.Exists 
                        ? "VIN already exists in inventory" 
                        : "VIN is available",
                    UnitId = vinCheck.UnitId,
                    StockNo = vinCheck.StockNo,
                    ResponseTimeMs = stopwatch.ElapsedMilliseconds,
                    Timestamp = DateTime.UtcNow
                };
                
                if (vinCheck.Exists)
                {
                    _logger.LogInformation("⚠️ VIN already exists - VIN: {vin}, UnitID: {unitId}", vin, vinCheck.UnitId);
                }
                else
                {
                    _logger.LogInformation("✅ VIN is available - VIN: {vin}", vin);
                }
                
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
                _logger.LogError(ex, "❌ Error in CheckVin function - {errorType}: {message}", 
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

        private async Task<VinCheckResult> CheckVinExists(string vin)
        {
            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                
                var query = "SELECT [UnitID], [StockNo] FROM [Units] WHERE [VIN] = @VIN";
                
                using var command = new SqlCommand(query, connection);
                command.Parameters.AddWithValue("@VIN", vin);
                command.CommandTimeout = 10;
                
                using var reader = await command.ExecuteReaderAsync();
                
                if (await reader.ReadAsync())
                {
                    return new VinCheckResult
                    {
                        Exists = true,
                        UnitId = reader.GetInt32(0),
                        StockNo = reader.IsDBNull(1) ? null : reader.GetString(1)
                    };
                }
                
                return new VinCheckResult
                {
                    Exists = false
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to check VIN: {vin}", vin);
                throw;
            }
        }
    }

    public class VinCheckResult
    {
        public bool Exists { get; set; }
        public int? UnitId { get; set; }
        public string? StockNo { get; set; }
    }
}
