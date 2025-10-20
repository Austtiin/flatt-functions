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
    public class SetStatus
    {
        private readonly ILogger<SetStatus> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public SetStatus(ILogger<SetStatus> logger, IConfiguration configuration)
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

        [Function("SetStatus")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "SetStatus/{id}")] HttpRequestData req,
            string id)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = req.CreateResponse();

            try
            {
                _logger.LogInformation("🔁 SetStatus started - UnitID: {id}", id);

                // CORS
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

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

                // Read and parse request body
                string requestBody = await req.ReadAsStringAsync() ?? "";
                if (string.IsNullOrWhiteSpace(requestBody))
                {
                    _logger.LogWarning("⚠️ Empty request body");
                    response.StatusCode = HttpStatusCode.BadRequest;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");

                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        Error = true,
                        Message = "Request body is required with 'status' field",
                        StatusCode = 400
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

                    return response;
                }

                SetStatusRequest? statusRequest;
                try
                {
                    statusRequest = JsonSerializer.Deserialize<SetStatusRequest>(requestBody, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        PropertyNameCaseInsensitive = true
                    });
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning("⚠️ Invalid JSON in request body: {error}", ex.Message);
                    response.StatusCode = HttpStatusCode.BadRequest;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");

                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        Error = true,
                        Message = "Invalid JSON in request body",
                        Details = ex.Message,
                        StatusCode = 400
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

                    return response;
                }

                if (statusRequest == null || string.IsNullOrWhiteSpace(statusRequest.Status))
                {
                    _logger.LogWarning("⚠️ Missing or empty status field");
                    response.StatusCode = HttpStatusCode.BadRequest;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");

                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        Error = true,
                        Message = "Status field is required. Valid values: 'Pending', 'Available', 'Sold'",
                        StatusCode = 400
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

                    return response;
                }

                // Validate status value
                string[] validStatuses = { "Pending", "Available", "Sold" };
                string status = statusRequest.Status.Trim();
                
                // Case-insensitive comparison but preserve exact casing for database
                var matchedStatus = validStatuses.FirstOrDefault(s => 
                    string.Equals(s, status, StringComparison.OrdinalIgnoreCase));
                
                if (matchedStatus == null)
                {
                    _logger.LogWarning("⚠️ Invalid status value: {status}", status);
                    response.StatusCode = HttpStatusCode.BadRequest;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");

                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        Error = true,
                        Message = $"Invalid status '{status}'. Valid values: 'Pending', 'Available', 'Sold'",
                        ProvidedStatus = status,
                        ValidStatuses = validStatuses,
                        StatusCode = 400
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

                    return response;
                }

                status = matchedStatus; // Use the correctly cased version

                // Check unit exists
                using (var conn = new SqlConnection(_connectionString))
                {
                    await conn.OpenAsync();

                    var checkCmd = new SqlCommand("SELECT COUNT(*) FROM [Units] WHERE [UnitID] = @UnitID", conn);
                    checkCmd.Parameters.AddWithValue("@UnitID", unitId);

                    var count = (int)(await checkCmd.ExecuteScalarAsync() ?? 0);
                    if (count == 0)
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

                    // Update status to 'Pending'
                    var updateCmd = new SqlCommand(@"
                        UPDATE [Units]
                        SET [Status] = @Status, [UpdatedAt] = GETDATE()
                        WHERE [UnitID] = @UnitID", conn);

                    updateCmd.Parameters.AddWithValue("@Status", status);
                    updateCmd.Parameters.AddWithValue("@UnitID", unitId);

                    var rows = await updateCmd.ExecuteNonQueryAsync();

                    stopwatch.Stop();

                    if (rows > 0)
                    {
                        _logger.LogInformation("✅ Unit {unitId} status set to {status}", unitId, status);
                        response.StatusCode = HttpStatusCode.OK;
                        response.Headers.Add("Content-Type", "application/json; charset=utf-8");

                        await response.WriteStringAsync(JsonSerializer.Serialize(new
                        {
                            Success = true,
                            Message = $"Status updated to '{status}'",
                            UnitId = unitId,
                            NewStatus = status,
                            ResponseTimeMs = stopwatch.ElapsedMilliseconds,
                            Timestamp = DateTime.UtcNow
                        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

                        return response;
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ No rows updated for UnitID {unitId}", unitId);
                        response.StatusCode = HttpStatusCode.InternalServerError;
                        response.Headers.Add("Content-Type", "application/json; charset=utf-8");

                        await response.WriteStringAsync(JsonSerializer.Serialize(new
                        {
                            Error = true,
                            Message = "Failed to update status",
                            StatusCode = 500
                        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

                        return response;
                    }
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "❌ Error in SetStatus - {message}", ex.Message);
                response.StatusCode = HttpStatusCode.InternalServerError;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");

                await response.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    Error = true,
                    Message = "An internal server error occurred while updating status",
                    Details = ex.Message,
                    StatusCode = 500
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

                return response;
            }
        }
    }

    public class SetStatusRequest
    {
        public string Status { get; set; } = string.Empty;
    }
}
