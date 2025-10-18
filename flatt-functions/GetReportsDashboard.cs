using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using System;
using Microsoft.Data.SqlClient;
using System.Diagnostics;

namespace flatt_functions
{
    public class GetReportsDashboard
    {
        private readonly ILogger<GetReportsDashboard> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public GetReportsDashboard(ILogger<GetReportsDashboard> logger, IConfiguration configuration)
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
            
            _logger.LogInformation("Database connection configured - Server: tcp:flatt-db-server.database.windows.net,1433, Database: flatt-inv-sql");
        }

        [Function("GetReportsDashboard")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "reports/dashboard")] HttpRequestData req)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = req.CreateResponse();
            
            try
            {
                _logger.LogInformation("📊 GetReportsDashboard function started - Request ID: {requestId}", Guid.NewGuid());
                
                // Add CORS headers
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                
                _logger.LogInformation("📈 Calculating reports dashboard statistics...");
                _logger.LogInformation("📊 Querying Units table for comprehensive reports...");
                
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                
                // Single optimized query to get all statistics
                var query = @"
                    SELECT 
                        ISNULL(SUM([Price]), 0) as TotalInventoryValue,
                        COUNT(*) as TotalItems,
                        SUM(CASE WHEN [TypeID] = 2 THEN 1 ELSE 0 END) as TotalVehicles,
                        SUM(CASE WHEN [TypeID] = 1 THEN 1 ELSE 0 END) as TotalFishHouses,
                        SUM(CASE WHEN [TypeID] = 3 THEN 1 ELSE 0 END) as TotalTrailers,
                        COUNT(DISTINCT [Make]) as TotalUniqueMakes,
                        SUM(CASE WHEN LOWER([Status]) = 'pending' THEN 1 ELSE 0 END) as PendingSales
                    FROM [Units]";
                
                using var command = new SqlCommand(query, connection);
                _logger.LogInformation("📝 Executing reports statistics query...");
                
                using var reader = await command.ExecuteReaderAsync();
                
                if (await reader.ReadAsync())
                {
                    var totalInventoryValue = reader.GetDecimal(0);
                    var totalItems = reader.GetInt32(1);
                    var totalVehicles = reader.GetInt32(2);
                    var totalFishHouses = reader.GetInt32(3);
                    var totalTrailers = reader.GetInt32(4);
                    var totalUniqueMakes = reader.GetInt32(5);
                    var pendingSales = reader.GetInt32(6);
                    
                    stopwatch.Stop();
                    
                    _logger.LogInformation("✅ Reports statistics retrieved successfully in {time}ms - Total Value: ${value:N2}, Vehicles: {vehicles}, Fish Houses: {fishHouses}, Trailers: {trailers}, Unique Makes: {makes}, Pending: {pending}", 
                        stopwatch.ElapsedMilliseconds, totalInventoryValue, totalVehicles, totalFishHouses, totalTrailers, totalUniqueMakes, pendingSales);
                    
                    var dashboardStats = new
                    {
                        TotalInventoryValue = totalInventoryValue,
                        TotalVehicles = totalVehicles,
                        TotalFishHouses = totalFishHouses,
                        TotalTrailers = totalTrailers,
                        TotalUniqueMakes = totalUniqueMakes,
                        PendingSales = pendingSales,
                        LastUpdated = DateTime.UtcNow,
                        ResponseTimeMs = stopwatch.ElapsedMilliseconds
                    };
                    
                    _logger.LogInformation("📊 Dashboard statistics calculated successfully");
                    _logger.LogInformation("🎉 Request completed successfully in {time}ms", stopwatch.ElapsedMilliseconds);
                    
                    response.StatusCode = HttpStatusCode.OK;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    
                    await response.WriteStringAsync(JsonSerializer.Serialize(dashboardStats, 
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                    
                    return response;
                }
                else
                {
                    stopwatch.Stop();
                    _logger.LogWarning("⚠️ No data found in Units table");
                    
                    response.StatusCode = HttpStatusCode.OK;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    
                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        TotalInventoryValue = 0,
                        TotalVehicles = 0,
                        TotalFishHouses = 0,
                        TotalTrailers = 0,
                        TotalUniqueMakes = 0,
                        PendingSales = 0,
                        LastUpdated = DateTime.UtcNow,
                        ResponseTimeMs = stopwatch.ElapsedMilliseconds
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                    
                    return response;
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "❌ Error in GetReportsDashboard function - {errorType}: {message}", 
                    ex.GetType().Name, ex.Message);
                
                response.StatusCode = HttpStatusCode.InternalServerError;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                
                await response.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    Error = true,
                    Message = "An internal server error occurred while calculating reports dashboard statistics",
                    Details = ex.Message,
                    StatusCode = 500,
                    Timestamp = DateTime.UtcNow
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                
                return response;
            }
        }
    }
}
