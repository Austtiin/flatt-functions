using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using Azure.Storage.Blobs;
using Azure;
using Azure.Identity;

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
            var ct = req.FunctionContext.CancellationToken;

            try
            {
                _logger.LogInformation("➕ AddInventory function started - Request ID: {requestId}", Guid.NewGuid());
                
                // Add CORS headers
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                
                // Prevent caching for write operations
                response.Headers.Add("Cache-Control", "no-store, no-cache, must-revalidate, max-age=0");
                response.Headers.Add("Pragma", "no-cache");
                response.Headers.Add("Expires", "Thu, 01 Jan 1970 00:00:00 GMT");
                
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
                
                // Use a single connection for the existence checks and insert.
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(ct);

                // Pre-flight existence checks for friendly, field-specific 409s.
                // These race against concurrent inserts, so the insert below also
                // defends against duplicates via the unique-violation catch.
                if (await CheckVinExists(connection, newVehicle.Vin!, ct))
                {
                    _logger.LogWarning("⚠️ VIN already exists: {vin}", newVehicle.Vin);
                    await FunctionHelpers.WriteJsonAsync(response, HttpStatusCode.Conflict, new
                    {
                        Error = true,
                        Message = $"VIN '{newVehicle.Vin}' already exists in inventory",
                        Field = "vin",
                        StatusCode = 409
                    }, ct);
                    return response;
                }

                // After confirming VIN availability, create a VIN folder in blob storage (best-effort)
                await TryCreateVinFolderAsync(newVehicle.Vin!);

                // Check if StockNo already exists (if provided)
                if (!string.IsNullOrWhiteSpace(newVehicle.StockNo) &&
                    await CheckStockNoExists(connection, newVehicle.StockNo, ct))
                {
                    _logger.LogWarning("⚠️ StockNo already exists: {stockNo}", newVehicle.StockNo);
                    await FunctionHelpers.WriteJsonAsync(response, HttpStatusCode.Conflict, new
                    {
                        Error = true,
                        Message = $"Stock Number '{newVehicle.StockNo}' already exists in inventory",
                        Field = "stockNo",
                        StatusCode = 409
                    }, ct);
                    return response;
                }

                // Insert vehicle into database. A unique constraint on VIN/StockNo may
                // still trip here if a concurrent request inserted the same value between
                // the check above and now (2627 = unique constraint, 2601 = unique index).
                int newUnitId;
                try
                {
                    newUnitId = await InsertVehicle(connection, newVehicle, ct);
                }
                catch (SqlException sqlEx) when (sqlEx.Number == 2627 || sqlEx.Number == 2601)
                {
                    _logger.LogWarning("⚠️ Duplicate VIN/StockNo detected on insert: {message}", sqlEx.Message);
                    await FunctionHelpers.WriteJsonAsync(response, HttpStatusCode.Conflict, new
                    {
                        Error = true,
                        Message = "A vehicle with this VIN or Stock Number already exists in inventory",
                        StatusCode = 409
                    }, ct);
                    return response;
                }

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
                    Mileage = newVehicle.Mileage,
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

        // replaced older TryCreateVinFolderAsync implementation with ResolveContainerClient-based version below
            private async Task TryCreateVinFolderAsync(string vin)
            {
                try
                {
                    var containerClient = ResolveContainerClient();
                    if (containerClient == null)
                    {
                        _logger.LogWarning("Blob storage not configured. Skipping VIN folder creation for {vin}", vin);
                        return;
                    }

                    // Ensure container exists (best-effort)
                    try { await containerClient.CreateIfNotExistsAsync(); } catch { /* ignore */ }

                    // No placeholder blob needed; folderless namespaces are created implicitly on first real upload
                    // Keep method as a no-op beyond ensuring the container exists
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create VIN folder for {vin}. Continuing without blocking.", vin);
                }
            }

            private BlobContainerClient? ResolveContainerClient()
            {
                // Prefer connection string if available (DEV)
                var connString =
                    _configuration["BlobConnectionString"] ??
                    _configuration.GetConnectionString("BlobConnectionString") ??
                    _configuration["ConnectionStrings:BlobConnectionString"];

                var baseUrl =
                    _configuration["BlobBaseURL"] ??
                    _configuration["Blob_URL"] ??
                    _configuration.GetConnectionString("BlobBaseURL") ??
                    _configuration.GetConnectionString("Blob_URL") ??
                    _configuration["ConnectionStrings:BlobBaseURL"] ??
                    _configuration["ConnectionStrings:Blob_URL"];

                if (!string.IsNullOrWhiteSpace(connString))
                {
                    // Derive container name from base URL if possible, else from explicit setting
                    var containerName = ExtractContainerName(baseUrl) ??
                                        _configuration["BlobContainerName"] ??
                                        _configuration.GetConnectionString("BlobContainerName") ??
                                        _configuration["ConnectionStrings:BlobContainerName"];
                    if (!string.IsNullOrWhiteSpace(containerName))
                    {
                        return new BlobContainerClient(connString!, containerName!);
                    }
                    // Fall back to service URL if baseUrl present
                }

                if (!string.IsNullOrWhiteSpace(baseUrl))
                {
                    // Use managed identity/DefaultAzureCredential
                    return new BlobContainerClient(new Uri(baseUrl!), new DefaultAzureCredential());
                }

                return null;
            }

            private string GetBlobPathPrefix()
            {
                // 1. Allow explicit override via configuration
                var explicitPrefix =
                    _configuration["BlobPathPrefix"] ??
                    _configuration.GetConnectionString("BlobPathPrefix") ??
                    _configuration["ConnectionStrings:BlobPathPrefix"];
                if (!string.IsNullOrWhiteSpace(explicitPrefix))
                {
                    var prefix = explicitPrefix!.Trim().Trim('/') + "/";
                    return prefix == "/" ? string.Empty : prefix;
                }

                var baseUrl =
                    _configuration["BlobBaseURL"] ??
                    _configuration["Blob_URL"] ??
                    _configuration.GetConnectionString("BlobBaseURL") ??
                    _configuration.GetConnectionString("Blob_URL") ??
                    _configuration["ConnectionStrings:BlobBaseURL"] ??
                    _configuration["ConnectionStrings:Blob_URL"];

                try
                {
                    if (string.IsNullOrWhiteSpace(baseUrl)) return string.Empty;
                    var uri = new Uri(baseUrl);
                    // segments: ["/", "container/", "optional-prefix/", ...]
                    if (uri.Segments.Length <= 2) return string.Empty;
                    var prefix = string.Join(string.Empty, uri.Segments.Skip(2));
                    // Normalize to ensure trailing slash if not empty
                    if (!string.IsNullOrEmpty(prefix) && !prefix.EndsWith("/")) prefix += "/";
                    return prefix;
                }
                catch
                {
                    return string.Empty;
                }
            }

            private static string? ExtractContainerName(string? url)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(url)) return null;
                    var uri = new Uri(url);
                    // Expecting https://account.blob.core.windows.net/container[/...]
                    if (uri.Segments.Length >= 2)
                    {
                        return uri.Segments[1].Trim('/');
                    }
                }
                catch { /* ignore parse issues */ }
                return null;
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

            if (vehicle.Mileage != null && vehicle.Mileage < 0)
                errors.Add("Mileage must be a positive number if provided");

            if (string.IsNullOrWhiteSpace(vehicle.Status))
                errors.Add("Status is required");
            
            if (string.IsNullOrWhiteSpace(vehicle.Color))
                errors.Add("Color is required");
            
            return errors;
        }

        private static async Task<bool> CheckVinExists(SqlConnection connection, string vin, CancellationToken ct)
        {
            var query = "SELECT COUNT(*) FROM [Units] WHERE [VIN] = @VIN";
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@VIN", vin);

            var result = await command.ExecuteScalarAsync(ct);
            return result != null && Convert.ToInt32(result) > 0;
        }

        private static async Task<bool> CheckStockNoExists(SqlConnection connection, string stockNo, CancellationToken ct)
        {
            var query = "SELECT COUNT(*) FROM [Units] WHERE [StockNo] = @StockNo";
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@StockNo", stockNo);

            var result = await command.ExecuteScalarAsync(ct);
            return result != null && Convert.ToInt32(result) > 0;
        }

        private static async Task<int> InsertVehicle(SqlConnection connection, AddVehicleRequest vehicle, CancellationToken ct)
        {
            var query = @"
                INSERT INTO [Units] (
                    [VIN], [StockNo], [Make], [Model], [Year], [Mileage], [Condition],
                    [Description], [Category], [TypeID],
                    [WidthCategory], [SizeCategory], [Price], [Status], [Color], [MSRP], [Banner]
                )
                OUTPUT INSERTED.UnitID
                VALUES (
                    @VIN, @StockNo, @Make, @Model, @Year, @Mileage, @Condition,
                    @Description, @Category, @TypeID,
                    @WidthCategory, @SizeCategory, @Price, @Status, @Color, @MSRP, @Banner
                )";
            
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@VIN", vehicle.Vin!);
            command.Parameters.AddWithValue("@StockNo", (object?)vehicle.StockNo ?? DBNull.Value);
            command.Parameters.AddWithValue("@Make", vehicle.Make!);
            command.Parameters.AddWithValue("@Model", vehicle.Model!);
            command.Parameters.AddWithValue("@Year", vehicle.Year!);
            command.Parameters.AddWithValue("@Mileage", (object?)vehicle.Mileage ?? DBNull.Value);
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
            command.Parameters.AddWithValue("@Banner", (object?)vehicle.Banner ?? DBNull.Value);

            var newId = await command.ExecuteScalarAsync(ct);
            return newId != null ? Convert.ToInt32(newId) : 0;
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

        [JsonConverter(typeof(FlexibleIntConverter))]
        public int? Mileage { get; set; }

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
