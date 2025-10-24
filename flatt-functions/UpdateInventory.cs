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
            
            var query = @"
                UPDATE [Units] 
                SET 
                    [VIN] = COALESCE(@VIN, [VIN]),
                    [StockNo] = COALESCE(@StockNo, [StockNo]),
                    [Make] = COALESCE(@Make, [Make]),
                    [Model] = COALESCE(@Model, [Model]),
                    [Year] = COALESCE(@Year, [Year]),
                    [Condition] = COALESCE(@Condition, [Condition]),
                    [Description] = COALESCE(@Description, [Description]),
                    [Category] = COALESCE(@Category, [Category]),
                    [TypeID] = COALESCE(@TypeID, [TypeID]),
                    [WidthCategory] = COALESCE(@WidthCategory, [WidthCategory]),
                    [SizeCategory] = COALESCE(@SizeCategory, [SizeCategory]),
                    [Price] = COALESCE(@Price, [Price]),
                    [Status] = COALESCE(@Status, [Status]),
                    [Color] = COALESCE(@Color, [Color]),
                    [UpdatedAt] = GETDATE()
                WHERE [UnitID] = @UnitID";
            
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@UnitID", unitId);
            command.Parameters.AddWithValue("@VIN", (object?)vehicle.Vin ?? DBNull.Value);
            command.Parameters.AddWithValue("@StockNo", (object?)vehicle.StockNo ?? DBNull.Value);
            command.Parameters.AddWithValue("@Make", (object?)vehicle.Make ?? DBNull.Value);
            command.Parameters.AddWithValue("@Model", (object?)vehicle.Model ?? DBNull.Value);
            command.Parameters.AddWithValue("@Year", (object?)vehicle.Year ?? DBNull.Value);
            command.Parameters.AddWithValue("@Condition", (object?)vehicle.Condition ?? DBNull.Value);
            command.Parameters.AddWithValue("@Description", (object?)vehicle.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("@Category", (object?)vehicle.Category ?? DBNull.Value);
            command.Parameters.AddWithValue("@TypeID", (object?)vehicle.TypeId ?? DBNull.Value);
            command.Parameters.AddWithValue("@WidthCategory", (object?)vehicle.WidthCategory ?? DBNull.Value);
            command.Parameters.AddWithValue("@SizeCategory", (object?)vehicle.SizeCategory ?? DBNull.Value);
            command.Parameters.AddWithValue("@Price", (object?)vehicle.Price ?? DBNull.Value);
            command.Parameters.AddWithValue("@Status", (object?)vehicle.Status ?? DBNull.Value);
            command.Parameters.AddWithValue("@Color", (object?)vehicle.Color ?? DBNull.Value);
            
            await command.ExecuteNonQueryAsync();
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
        
        public string? Status { get; set; }
        public string? Description { get; set; }
        public string? Color { get; set; }
    }
}
