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
using System.Data;
using System.Collections.Generic;
using System.Dynamic;
using System.Diagnostics;

namespace flatt_functions
{
    public class GetById
    {
        private readonly ILogger<GetById> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public GetById(ILogger<GetById> logger, IConfiguration configuration)
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

        [Function("GetById")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "vehicles/{id}")] HttpRequestData req,
            string id)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = req.CreateResponse();
            
            try
            {
                _logger.LogInformation("🚗 GetById function started - Request ID: {requestId}, Vehicle ID: {vehicleId}", 
                    Guid.NewGuid(), id);
                
                // Add CORS headers
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                
                // Validate ID parameter
                if (string.IsNullOrWhiteSpace(id))
                {
                    _logger.LogWarning("⚠️ Invalid vehicle ID provided: empty or null");
                    response.StatusCode = HttpStatusCode.BadRequest;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    
                    var errorResponse = new
                    {
                        Error = true,
                        Message = "Vehicle ID is required",
                        StatusCode = 400,
                        Timestamp = DateTime.UtcNow
                    };
                    
                    await response.WriteStringAsync(JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true
                    }));
                    
                    return response;
                }
                
                // Validate that ID is numeric
                if (!int.TryParse(id, out int unitId))
                {
                    _logger.LogWarning("⚠️ Invalid vehicle ID format provided: {id} (must be a number)", id);
                    response.StatusCode = HttpStatusCode.BadRequest;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    
                    var errorResponse = new
                    {
                        Error = true,
                        Message = "Vehicle ID must be a valid number",
                        ProvidedId = id,
                        StatusCode = 400,
                        Timestamp = DateTime.UtcNow
                    };
                    
                    await response.WriteStringAsync(JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true
                    }));
                    
                    return response;
                }
                
                _logger.LogInformation("🔍 Searching for vehicle with UnitID: {unitId}", unitId);
                
                // Get vehicle data
                var dataTimer = Stopwatch.StartNew();
                var vehicle = await GetVehicleByUnitId(unitId);
                dataTimer.Stop();
                
                if (vehicle == null)
                {
                    _logger.LogWarning("⚠️ Vehicle not found with UnitID: {unitId}", unitId);
                    response.StatusCode = HttpStatusCode.NotFound;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    
                    var notFoundResponse = new
                    {
                        Error = true,
                        Message = $"Vehicle with ID {unitId} not found",
                        UnitId = unitId,
                        StatusCode = 404,
                        Timestamp = DateTime.UtcNow
                    };
                    
                    await response.WriteStringAsync(JsonSerializer.Serialize(notFoundResponse, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true
                    }));
                    
                    return response;
                }
                
                _logger.LogInformation("✅ Vehicle found - UnitID: {unitId} retrieved in {ms}ms", unitId, dataTimer.ElapsedMilliseconds);
                
                // Prepare successful response
                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                
                var jsonResponse = JsonSerializer.Serialize((object)vehicle, jsonOptions);
                await response.WriteStringAsync(jsonResponse);
                
                stopwatch.Stop();
                _logger.LogInformation("🎉 Request completed successfully in {totalMs}ms", stopwatch.ElapsedMilliseconds);
                
                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "❌ Error in GetById function after {ms}ms - {errorType}: {message}", 
                    stopwatch.ElapsedMilliseconds, ex.GetType().Name, ex.Message);
                
                response.StatusCode = HttpStatusCode.InternalServerError;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                
                var errorResponse = new
                {
                    Error = true,
                    Message = "An internal server error occurred",
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

        private async Task<dynamic?> GetVehicleByUnitId(int unitId)
        {
            var timer = Stopwatch.StartNew();
            
            _logger.LogInformation("📊 Querying Units table for UnitID: {unitId}", unitId);
            
            try
            {
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                
                // Query for the specific vehicle by UnitID
                var query = @"
                    SELECT * 
                    FROM [Units] 
                    WHERE [UnitID] = @UnitID";
                
                _logger.LogInformation("📝 Executing query: {query}", query);
                
                using var command = new SqlCommand(query, connection);
                command.Parameters.AddWithValue("@UnitID", unitId);
                command.CommandTimeout = 30; // 30 second timeout
                
                using var reader = await command.ExecuteReaderAsync();
                
                if (!await reader.ReadAsync())
                {
                    timer.Stop();
                    _logger.LogInformation("❌ No vehicle found with UnitID: {unitId} in {ms}ms", 
                        unitId, timer.ElapsedMilliseconds);
                    return null;
                }
                
                // Get column names
                var columnNames = new List<string>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    columnNames.Add(reader.GetName(i));
                }
                
                _logger.LogInformation("📋 Query returned {columnCount} columns", columnNames.Count);
                
                // Read the single row
                var row = new ExpandoObject() as IDictionary<string, object>;
                
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    row[columnNames[i]] = value;
                }
                
                timer.Stop();
                _logger.LogInformation("✅ Successfully retrieved vehicle with UnitID: {unitId} in {ms}ms", 
                    unitId, timer.ElapsedMilliseconds);
                
                return row;
            }
            catch (Exception ex)
            {
                timer.Stop();
                _logger.LogError(ex, "❌ Data retrieval failed for UnitID: {unitId} after {ms}ms - {errorType}: {message}", 
                    unitId, timer.ElapsedMilliseconds, ex.GetType().Name, ex.Message);
                throw;
            }
        }
    }
}
