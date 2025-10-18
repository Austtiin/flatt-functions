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
    public class CheckDb
    {
        private readonly ILogger<CheckDb> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public CheckDb(ILogger<CheckDb> logger, IConfiguration configuration)
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
                _connectionString = string.Empty;
            }
            else
            {
                _connectionString = connectionString;
            }
        }

        [Function("CheckDb")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checkdb")] HttpRequestData req)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = req.CreateResponse();
            
            try
            {
                _logger.LogInformation("🔍 CheckDb function started - Request ID: {requestId}", Guid.NewGuid());
                
                // Add CORS headers
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                
                // Check if connection string is configured
                if (string.IsNullOrEmpty(_connectionString))
                {
                    _logger.LogError("❌ Database connection string not configured");
                    stopwatch.Stop();
                    
                    response.StatusCode = HttpStatusCode.OK;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    
                    var configErrorResponse = new
                    {
                        Connected = false,
                        Status = "Error",
                        Message = "Database connection string not configured",
                        ResponseTimeMs = stopwatch.ElapsedMilliseconds,
                        Timestamp = DateTime.UtcNow
                    };
                    
                    await response.WriteStringAsync(JsonSerializer.Serialize(configErrorResponse, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true
                    }));
                    
                    return response;
                }
                
                // Test database connection
                _logger.LogInformation("🔗 Testing database connection...");
                var connectionTest = await TestDatabaseConnection();
                
                stopwatch.Stop();
                
                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                
                var successResponse = new
                {
                    Connected = connectionTest.IsConnected,
                    Status = connectionTest.IsConnected ? "Healthy" : "Unhealthy",
                    Message = connectionTest.Message,
                    ResponseTimeMs = stopwatch.ElapsedMilliseconds,
                    DatabaseDetails = new
                    {
                        Server = connectionTest.ServerName,
                        Database = connectionTest.DatabaseName
                    },
                    Timestamp = DateTime.UtcNow
                };
                
                var jsonResponse = JsonSerializer.Serialize(successResponse, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                });
                
                await response.WriteStringAsync(jsonResponse);
                
                if (connectionTest.IsConnected)
                {
                    _logger.LogInformation("✅ Database connection successful in {ms}ms", stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    _logger.LogWarning("⚠️ Database connection failed in {ms}ms - {message}", 
                        stopwatch.ElapsedMilliseconds, connectionTest.Message);
                }
                
                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "❌ Error in CheckDb function after {ms}ms - {errorType}: {message}", 
                    stopwatch.ElapsedMilliseconds, ex.GetType().Name, ex.Message);
                
                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                
                var errorResponse = new
                {
                    Connected = false,
                    Status = "Error",
                    Message = $"Connection check failed: {ex.Message}",
                    ResponseTimeMs = stopwatch.ElapsedMilliseconds,
                    ErrorType = ex.GetType().Name,
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

        private async Task<DatabaseConnectionResult> TestDatabaseConnection()
        {
            var timer = Stopwatch.StartNew();
            
            try
            {
                using var connection = new SqlConnection(_connectionString);
                
                // Get server and database info from connection string
                var builder = new SqlConnectionStringBuilder(_connectionString);
                var serverName = builder.DataSource;
                var databaseName = builder.InitialCatalog;
                
                await connection.OpenAsync();
                
                // Execute a simple query to verify connection is working
                using var command = new SqlCommand("SELECT 1", connection);
                command.CommandTimeout = 5; // 5 second timeout for health check
                var result = await command.ExecuteScalarAsync();
                
                timer.Stop();
                
                _logger.LogInformation("✅ Database connection test successful - Server: {server}, Database: {database}, Time: {ms}ms", 
                    serverName, databaseName, timer.ElapsedMilliseconds);
                
                return new DatabaseConnectionResult
                {
                    IsConnected = true,
                    Message = "Database connection successful",
                    ServerName = serverName,
                    DatabaseName = databaseName,
                    ResponseTimeMs = timer.ElapsedMilliseconds
                };
            }
            catch (SqlException sqlEx)
            {
                timer.Stop();
                _logger.LogError(sqlEx, "❌ Database connection failed after {ms}ms - SQL Error: {message}", 
                    timer.ElapsedMilliseconds, sqlEx.Message);
                
                return new DatabaseConnectionResult
                {
                    IsConnected = false,
                    Message = $"SQL Error: {sqlEx.Message}",
                    ServerName = "Unknown",
                    DatabaseName = "Unknown",
                    ResponseTimeMs = timer.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                timer.Stop();
                _logger.LogError(ex, "❌ Database connection failed after {ms}ms - {errorType}: {message}", 
                    timer.ElapsedMilliseconds, ex.GetType().Name, ex.Message);
                
                return new DatabaseConnectionResult
                {
                    IsConnected = false,
                    Message = $"{ex.GetType().Name}: {ex.Message}",
                    ServerName = "Unknown",
                    DatabaseName = "Unknown",
                    ResponseTimeMs = timer.ElapsedMilliseconds
                };
            }
        }
    }

    public class DatabaseConnectionResult
    {
        public bool IsConnected { get; set; }
        public string Message { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public long ResponseTimeMs { get; set; }
    }
}
