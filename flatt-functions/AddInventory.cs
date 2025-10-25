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
    public class AddInventory
    {
        private readonly ILogger<AddInventory> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public AddInventory(ILogger<AddInventory> logger, IConfiguration configuration)
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

        [Function("AddInventory")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "vehicles/add")] HttpRequestData req)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = req.CreateResponse();
            
            try
            {
                _logger.LogInformation("➕ AddInventory function started - Request ID: {requestId}", Guid.NewGuid());
                
                // Add CORS headers
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                
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
                
                var newVehicle = JsonSerializer.Deserialize<AddVehicleRequest>(requestBody, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                });
                
                if (newVehicle == null)
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
                if (!string.IsNullOrWhiteSpace(newVehicle.Vin))
                {
                    newVehicle.Vin = newVehicle.Vin.ToUpper().Trim();
                }
                
                if (!string.IsNullOrWhiteSpace(newVehicle.StockNo))
                {
                    newVehicle.StockNo = newVehicle.StockNo.ToUpper().Trim();
                }

                // Validate required fields
                var validationErrors = ValidateVehicle(newVehicle);
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
                
                // Check if VIN already exists
                var vinExists = await CheckVinExists(newVehicle.Vin!);
                if (vinExists)
                {
                    _logger.LogWarning("⚠️ VIN already exists: {vin}", newVehicle.Vin);
                    response.StatusCode = HttpStatusCode.Conflict;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    
                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        Error = true,
                        Message = $"VIN '{newVehicle.Vin}' already exists in inventory",
                        Field = "vin",
                        StatusCode = 409
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                    
                    return response;
                }
                
                // Check if StockNo already exists (if provided)
                if (!string.IsNullOrWhiteSpace(newVehicle.StockNo))
                {
                    var stockNoExists = await CheckStockNoExists(newVehicle.StockNo);
                    if (stockNoExists)
                    {
                        _logger.LogWarning("⚠️ StockNo already exists: {stockNo}", newVehicle.StockNo);
                        response.StatusCode = HttpStatusCode.Conflict;
                        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                        
                        await response.WriteStringAsync(JsonSerializer.Serialize(new
                        {
                            Error = true,
                            Message = $"Stock Number '{newVehicle.StockNo}' already exists in inventory",
                            Field = "stockNo",
                            StatusCode = 409
                        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                        
                        return response;
                    }
                }
                
                // Insert vehicle into database
                var newUnitId = await InsertVehicle(newVehicle);
                
                stopwatch.Stop();
                
                _logger.LogInformation("✅ Vehicle added successfully - UnitID: {unitId}, VIN: {vin}, StockNo: {stockNo}, Color: {color}", 
                    newUnitId, newVehicle.Vin, newVehicle.StockNo, newVehicle.Color);

                // Log all fields that were added for traceability
                var addedDetails = new
                {
                    UnitId = newUnitId,
                    Vin = newVehicle.Vin,
                    StockNo = newVehicle.StockNo,
                    Make = newVehicle.Make,
                    Model = newVehicle.Model,
                    Year = newVehicle.Year,
                    Condition = newVehicle.Condition,
                    Description = newVehicle.Description,
                    Category = newVehicle.Category,
                    TypeId = newVehicle.TypeId,
                    WidthCategory = newVehicle.WidthCategory,
                    SizeCategory = newVehicle.SizeCategory,
                    Price = newVehicle.Price,
                    Msrp = newVehicle.Msrp,
                    Status = newVehicle.Status,
                    Color = newVehicle.Color
                };

                var logJsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
                };
                var addedDetailsJson = JsonSerializer.Serialize(addedDetails, logJsonOptions);
                _logger.LogInformation("🧾 Added vehicle details: {json}", addedDetailsJson);
                
                response.StatusCode = HttpStatusCode.Created;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                response.Headers.Add("Location", $"/api/vehicles/{newUnitId}");
                
                await response.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    Success = true,
                    Message = "Vehicle added successfully",
                    UnitId = newUnitId,
                    Vin = newVehicle.Vin,
                    StockNo = newVehicle.StockNo,
                    Color = newVehicle.Color,
                    ResponseTimeMs = stopwatch.ElapsedMilliseconds,
                    Timestamp = DateTime.UtcNow
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                
                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "❌ Error in AddInventory function - {errorType}: {message}", 
                    ex.GetType().Name, ex.Message);
                
                response.StatusCode = HttpStatusCode.InternalServerError;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                
                await response.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    Error = true,
                    Message = "An internal server error occurred while adding vehicle",
                    Details = ex.Message,
                    StatusCode = 500
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                
                return response;
            }
        }

        private System.Collections.Generic.List<string> ValidateVehicle(AddVehicleRequest vehicle)
        {
            var errors = new System.Collections.Generic.List<string>();
            
            if (string.IsNullOrWhiteSpace(vehicle.Vin))
                errors.Add("VIN is required");
            
            if (vehicle.Year == null || vehicle.Year < 1900 || vehicle.Year > DateTime.Now.Year + 2)
                errors.Add($"Year must be between 1900 and {DateTime.Now.Year + 2}");
            
            if (string.IsNullOrWhiteSpace(vehicle.Make))
                errors.Add("Make is required");
            
            if (string.IsNullOrWhiteSpace(vehicle.Model))
                errors.Add("Model is required");
            
            if (vehicle.TypeId == null)
                errors.Add("TypeID is required");
            
            if (vehicle.Price == null || vehicle.Price < 0)
                errors.Add("Price must be a positive number");
            
            if (vehicle.Msrp != null && vehicle.Msrp < 0)
                errors.Add("MSRP must be a positive number if provided");
            
            if (string.IsNullOrWhiteSpace(vehicle.Status))
                errors.Add("Status is required");
            
            if (string.IsNullOrWhiteSpace(vehicle.Color))
                errors.Add("Color is required");
            
            return errors;
        }

        private async Task<bool> CheckVinExists(string vin)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            
            var query = "SELECT COUNT(*) FROM [Units] WHERE [VIN] = @VIN";
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@VIN", vin);
            
            var result = await command.ExecuteScalarAsync();
            var count = result != null ? (int)result : 0;
            return count > 0;
        }

        private async Task<bool> CheckStockNoExists(string stockNo)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            
            var query = "SELECT COUNT(*) FROM [Units] WHERE [StockNo] = @StockNo";
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@StockNo", stockNo);
            
            var result = await command.ExecuteScalarAsync();
            var count = result != null ? (int)result : 0;
            return count > 0;
        }

        private async Task<int> InsertVehicle(AddVehicleRequest vehicle)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            
            var query = @"
                INSERT INTO [Units] (
                    [VIN], [StockNo], [Make], [Model], [Year], [Condition], 
                    [Description], [Category], [TypeID], 
                    [WidthCategory], [SizeCategory], [Price], [Status], [Color], [MSRP]
                )
                OUTPUT INSERTED.UnitID
                VALUES (
                    @VIN, @StockNo, @Make, @Model, @Year, @Condition, 
                    @Description, @Category, @TypeID, 
                    @WidthCategory, @SizeCategory, @Price, @Status, @Color, @MSRP
                )";
            
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@VIN", vehicle.Vin!);
            command.Parameters.AddWithValue("@StockNo", (object?)vehicle.StockNo ?? DBNull.Value);
            command.Parameters.AddWithValue("@Make", vehicle.Make!);
            command.Parameters.AddWithValue("@Model", vehicle.Model!);
            command.Parameters.AddWithValue("@Year", vehicle.Year!);
            command.Parameters.AddWithValue("@Condition", (object?)vehicle.Condition ?? DBNull.Value);
            command.Parameters.AddWithValue("@Description", (object?)vehicle.Description ?? DBNull.Value);
            
            command.Parameters.AddWithValue("@Category", (object?)vehicle.Category ?? DBNull.Value);
            command.Parameters.AddWithValue("@TypeID", vehicle.TypeId!);
            command.Parameters.AddWithValue("@WidthCategory", (object?)vehicle.WidthCategory ?? DBNull.Value);
            command.Parameters.AddWithValue("@SizeCategory", (object?)vehicle.SizeCategory ?? DBNull.Value);
            command.Parameters.AddWithValue("@Price", vehicle.Price!);
            command.Parameters.AddWithValue("@Status", vehicle.Status!);
            command.Parameters.AddWithValue("@Color", vehicle.Color!);
            command.Parameters.AddWithValue("@MSRP", (object?)vehicle.Msrp ?? DBNull.Value);
            
            var newId = await command.ExecuteScalarAsync();
            return newId != null ? (int)newId : 0;
        }
    }

    public class AddVehicleRequest
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
    }

    public class FlexibleIntConverter : JsonConverter<int?>
    {
        public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;
            
            if (reader.TokenType == JsonTokenType.Number)
                return reader.GetInt32();
            
            if (reader.TokenType == JsonTokenType.String)
            {
                var stringValue = reader.GetString();
                if (string.IsNullOrEmpty(stringValue))
                    return null;
                
                if (int.TryParse(stringValue, out int result))
                    return result;
            }
            
            throw new JsonException($"Unable to convert '{reader.GetString()}' to integer");
        }

        public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
        {
            if (value == null)
                writer.WriteNullValue();
            else
                writer.WriteNumberValue(value.Value);
        }
    }

    public class FlexibleDecimalConverter : JsonConverter<decimal?>
    {
        public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;
            
            if (reader.TokenType == JsonTokenType.Number)
                return reader.GetDecimal();
            
            if (reader.TokenType == JsonTokenType.String)
            {
                var stringValue = reader.GetString();
                if (string.IsNullOrEmpty(stringValue))
                    return null;
                
                if (decimal.TryParse(stringValue, out decimal result))
                    return result;
            }
            
            throw new JsonException($"Unable to convert '{reader.GetString()}' to decimal");
        }

        public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
        {
            if (value == null)
                writer.WriteNullValue();
            else
                writer.WriteNumberValue(value.Value);
        }
    }
}
