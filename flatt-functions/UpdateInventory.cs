using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using System.Linq;
using System;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.IO;

namespace flatt_functions
{
    public class UpdateInventory
    {
        private readonly ILogger<UpdateInventory> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public UpdateInventory(ILogger<UpdateInventory> logger, IConfiguration configuration)
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

        [Function("UpdateInventory")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "vehicles/{id}")] HttpRequestData req,
            string id)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = req.CreateResponse();
            
            try
            {
                _logger.LogInformation("🔄 UpdateInventory function started - UnitID: {id}, Request ID: {requestId}", 
                    id, Guid.NewGuid());
                
                // Add CORS headers
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, PUT, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                
                // Prevent caching for write operations
                response.Headers.Add("Cache-Control", "no-store, no-cache, must-revalidate, max-age=0");
                response.Headers.Add("Pragma", "no-cache");
                response.Headers.Add("Expires", "Thu, 01 Jan 1970 00:00:00 GMT");
                
                // Validate ID parameter
                if (!int.TryParse(id, out int unitId))
                {
                    _logger.LogWarning("⚠️ Invalid UnitID format: {id}", id);
                    response.StatusCode = HttpStatusCode.BadRequest;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    
                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        Error = true,
                        Message = "Invalid UnitID format. Must be a number.",
                        StatusCode = 400
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                    
                    return response;
                }
                
                // Check if unit exists
                var exists = await CheckUnitExists(unitId);
                if (!exists)
                {
                    _logger.LogWarning("⚠️ Unit not found: {unitId}", unitId);
                    response.StatusCode = HttpStatusCode.NotFound;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    
                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        Error = true,
                        Message = $"Vehicle with UnitID {unitId} not found",
                        StatusCode = 404
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                    
                    return response;
                }
                
                // Read and parse request body
                string requestBody;
                using (var reader = new StreamReader(req.Body))
                {
                    requestBody = await reader.ReadToEndAsync();
                }
                
                if (string.IsNullOrWhiteSpace(requestBody))
                {
                    _logger.LogWarning("⚠️ Empty request body");
                    response.StatusCode = HttpStatusCode.BadRequest;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    
                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        Error = true,
                        Message = "Request body is required",
                        StatusCode = 400
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                    
                    return response;
                }
                
                var updateData = JsonSerializer.Deserialize<UpdateVehicleRequest>(requestBody, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                });
                
                if (updateData == null)
                {
                    _logger.LogWarning("⚠️ Failed to parse request body");
                    response.StatusCode = HttpStatusCode.BadRequest;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    
                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        Error = true,
                        Message = "Invalid JSON format",
                        StatusCode = 400
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                    
                    return response;
                }
                
                // DEBUG LOGGING - Log what data was received
                _logger.LogInformation("📝 Update data received for UnitID {unitId}: VIN={vin}, Make={make}, Model={model}, Banner={banner}, StockNo={stockNo}, Year={year}, Price={price}, Status={status}", 
                    unitId, 
                    updateData.Vin ?? "NULL", 
                    updateData.Make ?? "NULL", 
                    updateData.Model ?? "NULL",
                    updateData.Banner ?? "NULL",
                    updateData.StockNo ?? "NULL",
                    updateData.Year?.ToString() ?? "NULL",
                    updateData.Price?.ToString() ?? "NULL",
                    updateData.Status ?? "NULL");
                
                // Normalize VIN and StockNo to uppercase
                if (!string.IsNullOrWhiteSpace(updateData.Vin))
                {
                    updateData.Vin = updateData.Vin.ToUpper().Trim();
                }
                
                if (!string.IsNullOrWhiteSpace(updateData.StockNo))
                {
                    updateData.StockNo = updateData.StockNo.ToUpper().Trim();
                }

                // Validate required fields if provided
                var validationErrors = ValidateUpdateData(updateData);
                if (validationErrors.Any())
                {
                    _logger.LogWarning("⚠️ Validation failed: {errors}", string.Join(", ", validationErrors));
                    response.StatusCode = HttpStatusCode.BadRequest;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    
                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        Error = true,
                        Message = "Validation failed",
                        Errors = validationErrors,
                        StatusCode = 400
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                    
                    return response;
                }
                
                // Check for duplicate VIN (if VIN is being updated)
                if (!string.IsNullOrWhiteSpace(updateData.Vin))
                {
                    var vinExistsForOtherUnit = await CheckVinExistsForOtherUnit(updateData.Vin, unitId);
                    if (vinExistsForOtherUnit)
                    {
                        _logger.LogWarning("⚠️ VIN already exists for another unit: {vin}", updateData.Vin);
                        response.StatusCode = HttpStatusCode.Conflict;
                        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                        
                        await response.WriteStringAsync(JsonSerializer.Serialize(new
                        {
                            Error = true,
                            Message = $"VIN '{updateData.Vin}' already exists for another vehicle",
                            Field = "vin",
                            StatusCode = 409
                        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                        
                        return response;
                    }
                }
                
                // Check for duplicate StockNo (if StockNo is being updated)
                if (!string.IsNullOrWhiteSpace(updateData.StockNo))
                {
                    var stockNoExistsForOtherUnit = await CheckStockNoExistsForOtherUnit(updateData.StockNo, unitId);
                    if (stockNoExistsForOtherUnit)
                    {
                        _logger.LogWarning("⚠️ StockNo already exists for another unit: {stockNo}", updateData.StockNo);
                        response.StatusCode = HttpStatusCode.Conflict;
                        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                        
                        await response.WriteStringAsync(JsonSerializer.Serialize(new
                        {
                            Error = true,
                            Message = $"Stock Number '{updateData.StockNo}' already exists for another vehicle",
                            Field = "stockNo",
                            StatusCode = 409
                        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                        
                        return response;
                    }
                }
                
                // Update vehicle in database
                await UpdateVehicle(unitId, updateData);
                
                stopwatch.Stop();
                
                _logger.LogInformation("✅ Vehicle updated successfully - UnitID: {unitId}", unitId);
                
                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                
                await response.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    Success = true,
                    Message = "Vehicle updated successfully",
                    UnitId = unitId,
                    ResponseTimeMs = stopwatch.ElapsedMilliseconds,
                    Timestamp = DateTime.UtcNow
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                
                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "❌ Error in UpdateInventory function - {errorType}: {message}", 
                    ex.GetType().Name, ex.Message);
                
                response.StatusCode = HttpStatusCode.InternalServerError;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                
                await response.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    Error = true,
                    Message = "An internal server error occurred while updating vehicle",
                    Details = ex.Message,
                    StatusCode = 500
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                
                return response;
            }
        }

        private System.Collections.Generic.List<string> ValidateUpdateData(UpdateVehicleRequest vehicle)
        {
            var errors = new System.Collections.Generic.List<string>();
            
            if (vehicle.Year != null && (vehicle.Year < 1900 || vehicle.Year > DateTime.Now.Year + 2))
                errors.Add($"Year must be between 1900 and {DateTime.Now.Year + 2}");
            
            if (vehicle.Price != null && vehicle.Price < 0)
                errors.Add("Price must be a positive number");

            if (vehicle.Msrp != null && vehicle.Msrp < 0)
                errors.Add("MSRP must be a positive number");
            
            return errors;
        }

        private async Task<bool> CheckUnitExists(int unitId)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            
            var query = "SELECT COUNT(*) FROM [Units] WHERE [UnitID] = @UnitID";
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@UnitID", unitId);
            
            var result = await command.ExecuteScalarAsync();
            var count = result != null ? (int)result : 0;
            return count > 0;
        }

        private async Task<bool> CheckVinExistsForOtherUnit(string vin, int unitId)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            
            var query = "SELECT COUNT(*) FROM [Units] WHERE [VIN] = @VIN AND [UnitID] != @UnitID";
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@VIN", vin);
            command.Parameters.AddWithValue("@UnitID", unitId);
            
            var result = await command.ExecuteScalarAsync();
            var count = result != null ? (int)result : 0;
            return count > 0;
        }

        private async Task<bool> CheckStockNoExistsForOtherUnit(string stockNo, int unitId)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            
            var query = "SELECT COUNT(*) FROM [Units] WHERE [StockNo] = @StockNo AND [UnitID] != @UnitID";
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@StockNo", stockNo);
            command.Parameters.AddWithValue("@UnitID", unitId);
            
            var result = await command.ExecuteScalarAsync();
            var count = result != null ? (int)result : 0;
            return count > 0;
        }

        private async Task UpdateVehicle(int unitId, UpdateVehicleRequest vehicle)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            
            // Build dynamic query to only update fields that are provided (not null)
            var setClauses = new List<string>();
            var command = new SqlCommand();
            
            // Always update UpdatedAt
            setClauses.Add("[UpdatedAt] = GETDATE()");
            
            // Only add SET clauses for non-null properties
            if (vehicle.Vin != null)
            {
                setClauses.Add("[VIN] = @VIN");
                command.Parameters.AddWithValue("@VIN", vehicle.Vin);
            }
            
            if (vehicle.StockNo != null)
            {
                setClauses.Add("[StockNo] = @StockNo");
                command.Parameters.AddWithValue("@StockNo", vehicle.StockNo);
            }
            
            if (vehicle.Make != null)
            {
                setClauses.Add("[Make] = @Make");
                command.Parameters.AddWithValue("@Make", vehicle.Make);
            }
            
            if (vehicle.Model != null)
            {
                setClauses.Add("[Model] = @Model");
                command.Parameters.AddWithValue("@Model", vehicle.Model);
            }
            
            if (vehicle.Year != null)
            {
                setClauses.Add("[Year] = @Year");
                command.Parameters.AddWithValue("@Year", vehicle.Year);
            }
            
            if (vehicle.Condition != null)
            {
                setClauses.Add("[Condition] = @Condition");
                command.Parameters.AddWithValue("@Condition", vehicle.Condition);
            }
            
            if (vehicle.Description != null)
            {
                setClauses.Add("[Description] = @Description");
                command.Parameters.AddWithValue("@Description", vehicle.Description);
            }
            
            if (vehicle.Category != null)
            {
                setClauses.Add("[Category] = @Category");
                command.Parameters.AddWithValue("@Category", vehicle.Category);
            }
            
            if (vehicle.TypeId != null)
            {
                setClauses.Add("[TypeID] = @TypeID");
                command.Parameters.AddWithValue("@TypeID", vehicle.TypeId);
            }
            
            if (vehicle.WidthCategory != null)
            {
                setClauses.Add("[WidthCategory] = @WidthCategory");
                command.Parameters.AddWithValue("@WidthCategory", vehicle.WidthCategory);
            }
            
            if (vehicle.SizeCategory != null)
            {
                setClauses.Add("[SizeCategory] = @SizeCategory");
                command.Parameters.AddWithValue("@SizeCategory", vehicle.SizeCategory);
            }
            
            if (vehicle.Price != null)
            {
                setClauses.Add("[Price] = @Price");
                command.Parameters.AddWithValue("@Price", vehicle.Price);
            }
            
            if (vehicle.Msrp != null)
            {
                setClauses.Add("[MSRP] = @MSRP");
                command.Parameters.AddWithValue("@MSRP", vehicle.Msrp);
            }
            
            if (vehicle.Status != null)
            {
                setClauses.Add("[Status] = @Status");
                command.Parameters.AddWithValue("@Status", vehicle.Status);
            }
            
            if (vehicle.Color != null)
            {
                setClauses.Add("[Color] = @Color");
                command.Parameters.AddWithValue("@Color", vehicle.Color);
            }
            
            if (vehicle.Banner != null)
            {
                setClauses.Add("[Banner] = @Banner");
                command.Parameters.AddWithValue("@Banner", vehicle.Banner);
            }
            
            // If no fields to update except UpdatedAt, just update UpdatedAt
            if (setClauses.Count == 1)
            {
                _logger.LogWarning("⚠️ No fields to update for UnitID {unitId}", unitId);
            }
            
            var query = $@"
                UPDATE [Units] 
                SET {string.Join(", ", setClauses)}
                WHERE [UnitID] = @UnitID";
            
            _logger.LogInformation("🔧 Executing UPDATE query for UnitID {unitId} with {count} field updates", unitId, setClauses.Count - 1);
            _logger.LogDebug("SQL Query: {query}", query);
            
            command.CommandText = query;
            command.Connection = connection;
            command.Parameters.AddWithValue("@UnitID", unitId);
            
            var rowsAffected = await command.ExecuteNonQueryAsync();
            
            if (rowsAffected != 1)
            {
                _logger.LogError("⚠️ CRITICAL: Expected to update 1 row but updated {count} rows for UnitID {unitId}!", rowsAffected, unitId);
                throw new InvalidOperationException($"Update affected {rowsAffected} rows instead of 1. Database may be corrupted.");
            }
            
            _logger.LogInformation("✅ Successfully updated {count} rows for UnitID {unitId}", rowsAffected, unitId);
        }
    }

    public class UpdateVehicleRequest
    {
        public string? Vin { get; set; }
        
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int? Year { get; set; }
        
        public string? Make { get; set; }
        public string? Model { get; set; }
        public string? StockNo { get; set; }
        public string? Condition { get; set; }
        public string? Category { get; set; }
        
        [JsonConverter(typeof(FlexibleIntConverter))]
        public int? TypeId { get; set; }
        
        public string? WidthCategory { get; set; }
        public string? SizeCategory { get; set; }
        
    [JsonConverter(typeof(FlexibleDecimalConverter))]
    public decimal? Price { get; set; }
        
    [JsonConverter(typeof(FlexibleDecimalConverter))]
    public decimal? Msrp { get; set; }
        
        public string? Status { get; set; }
        public string? Description { get; set; }
        public string? Color { get; set; }
        public string? Banner { get; set; }
    }
}
