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
    public class GetDashboardStats
    {
        private readonly ILogger<GetDashboardStats> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public GetDashboardStats(ILogger<GetDashboardStats> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            
            // Get connection string
            var connectionString = configuration["SqlConnectionString"] ?? 
                                  configuration.GetConnectionString("SqlConnectionString") ??
                                  configuration["ConnectionStrings:SqlConnectionString"];
            
            if (string.IsNullOrEmpty(connectionString))
            {
                _logger.LogError("SqlConnectionString is null or empty in configuration");
                _logger.LogError("Available configuration keys: {keys}", 
                    string.Join(", ", configuration.AsEnumerable().Select(x => x.Key)));
                throw new InvalidOperationException("SqlConnectionString not set in configuration.");
            }
            
            // Log connection string details (safely)
            var connStringBuilder = new SqlConnectionStringBuilder(connectionString);
            _logger.LogInformation("Database connection configured - Server: {server}, Database: {database}", 
                connStringBuilder.DataSource, 
                connStringBuilder.InitialCatalog);
            
            _connectionString = connectionString;
        }

        [Function("GetDashboardStats")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard/stats")] HttpRequestData req)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = req.CreateResponse();
            
            try
            {
                _logger.LogInformation("📊 GetDashboardStats function started - Request ID: {requestId}", Guid.NewGuid());
                
                // Add CORS headers
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                
                // Get dashboard statistics
                _logger.LogInformation("📈 Calculating dashboard statistics...");
                var stats = await GetDashboardStatistics();
                
                _logger.LogInformation("✅ Dashboard statistics calculated successfully - Total Items: {total}, Total Value: ${value}, Available: {available}", 
                    stats.TotalItems, stats.TotalValue, stats.AvailableItems);
                
                // Prepare successful response
                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                
                var jsonResponse = JsonSerializer.Serialize(stats, jsonOptions);
                await response.WriteStringAsync(jsonResponse);
                
                stopwatch.Stop();
                _logger.LogInformation("🎉 Request completed successfully in {totalMs}ms", stopwatch.ElapsedMilliseconds);
                
                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "❌ Error in GetDashboardStats function after {ms}ms - {errorType}: {message}", 
                    stopwatch.ElapsedMilliseconds, ex.GetType().Name, ex.Message);
                
                response.StatusCode = HttpStatusCode.InternalServerError;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                
                var errorResponse = new
                {
                    Error = true,
                    Message = "An internal server error occurred while fetching dashboard statistics",
                    Details = ex.Message,
                    Type = ex.GetType().Name,
                    StatusCode = 500,
                    Timestamp = DateTime.UtcNow
                };
                
                var errorJson = JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                });
                
                await response.WriteStringAsync(errorJson);
                return response;
            }
        }

        private async Task<DashboardStats> GetDashboardStatistics()
        {
            var timer = Stopwatch.StartNew();
            
            _logger.LogInformation("📊 Querying Units table for dashboard statistics...");
            
            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                
                // Query to get all statistics in a single database call
                var query = @"
                    SELECT 
                        COUNT(*) AS TotalItems,
                        ISNULL(SUM(CAST([Price] AS DECIMAL(18,2))), 0) AS TotalValue,
                        COUNT(CASE WHEN LOWER([Status]) = 'available' THEN 1 END) AS AvailableItems
                    FROM [Units]";
                
                _logger.LogInformation("📝 Executing statistics query...");
                
                using var command = new SqlCommand(query, connection);
                command.CommandTimeout = 30; // 30 second timeout
                
                using var reader = await command.ExecuteReaderAsync();
                
                if (!await reader.ReadAsync())
                {
                    timer.Stop();
                    _logger.LogWarning("⚠️ No data returned from statistics query");
                    return new DashboardStats
                    {
                        TotalItems = 0,
                        TotalValue = 0,
                        AvailableItems = 0,
                        LastUpdated = DateTime.UtcNow
                    };
                }
                
                var totalItems = reader.GetInt32(0);
                var totalValue = reader.GetDecimal(1);
                var availableItems = reader.GetInt32(2);
                
                timer.Stop();
                
                _logger.LogInformation("✅ Statistics retrieved successfully in {ms}ms - Total: {total}, Value: ${value}, Available: {available}", 
                    timer.ElapsedMilliseconds, totalItems, totalValue, availableItems);
                
                return new DashboardStats
                {
                    TotalItems = totalItems,
                    TotalValue = totalValue,
                    AvailableItems = availableItems,
                    LastUpdated = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                timer.Stop();
                _logger.LogError(ex, "❌ Failed to retrieve dashboard statistics after {ms}ms - {errorType}: {message}", 
                    timer.ElapsedMilliseconds, ex.GetType().Name, ex.Message);
                throw;
            }
        }
    }

    public class DashboardStats
    {
        public int TotalItems { get; set; }
        public decimal TotalValue { get; set; }
        public int AvailableItems { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}
