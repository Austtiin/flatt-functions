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
using Azure.Storage.Blobs;
using Azure.Identity;
using Azure;

namespace flatt_functions
{
    public class DeleteInventory
    {
        private readonly ILogger<DeleteInventory> _logger;
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public DeleteInventory(ILogger<DeleteInventory> logger, IConfiguration configuration)
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

        // Route example: /api/vehicles/delete/123
        [Function("DeleteInventory")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", "get", Route = "vehicles/delete/{id}")] HttpRequestData req,
            string id)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = req.CreateResponse();

            try
            {
                _logger.LogInformation("🗑️ DeleteInventory function started - UnitID: {id}", id);

                // CORS
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, DELETE, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                // Validate UnitID
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

                // Confirm unit exists
                if (!await CheckUnitExists(unitId))
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

                // Capture VIN before deletion for blob cleanup
                var vin = await GetVinForUnit(unitId);

                // Delete DB row
                var deleted = await DeleteUnit(unitId);

                stopwatch.Stop();

                if (deleted)
                {
                    // Best-effort: delete VIN folder in blob storage
                    if (!string.IsNullOrWhiteSpace(vin))
                    {
                        _ = TryDeleteVinFolderAsync(vin!);
                    }
                    _logger.LogInformation("✅ Vehicle deleted successfully - UnitID: {unitId}", unitId);
                    response.StatusCode = HttpStatusCode.OK;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        Success = true,
                        Message = "Vehicle deleted successfully",
                        UnitId = unitId,
                        ResponseTimeMs = stopwatch.ElapsedMilliseconds,
                        Timestamp = DateTime.UtcNow
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                }
                else
                {
                    // Should not happen because we checked existence, but handle gracefully
                    _logger.LogWarning("⚠️ Delete reported no rows affected - UnitID: {unitId}", unitId);
                    response.StatusCode = HttpStatusCode.NotFound;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        Error = true,
                        Message = $"No rows deleted for UnitID {unitId}",
                        StatusCode = 404
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                }

                return response;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "❌ Error in DeleteInventory - {error}: {message}", ex.GetType().Name, ex.Message);
                response.StatusCode = HttpStatusCode.InternalServerError;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await response.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    Error = true,
                    Message = "An internal server error occurred while deleting vehicle",
                    Details = ex.Message,
                    StatusCode = 500
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                return response;
            }
        }

        private async Task<string?> GetVinForUnit(int unitId)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            var query = "SELECT [VIN] FROM [Units] WHERE [UnitID] = @UnitID";
            using var cmd = new SqlCommand(query, connection);
            cmd.Parameters.AddWithValue("@UnitID", unitId);
            var result = await cmd.ExecuteScalarAsync();
            return result as string;
        }

        private async Task TryDeleteVinFolderAsync(string vin)
        {
            try
            {
                var containerClient = ResolveContainerClient();
                if (containerClient == null)
                {
                    _logger.LogWarning("Blob storage not configured. Skipping VIN folder deletion for {vin}", vin);
                    return;
                }

                var basePrefix = GetBlobPathPrefix();
                var prefix = basePrefix + vin + "/";
                await foreach (var blob in containerClient.GetBlobsAsync(prefix: prefix))
                {
                    try
                    {
                        await containerClient.DeleteBlobIfExistsAsync(blob.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed deleting blob {name} for VIN {vin}", blob.Name, vin);
                    }
                }
                _logger.LogInformation("Deleted VIN folder and blobs for {vin} (prefix: '{prefixUsed}')", vin, basePrefix);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete VIN folder for {vin}. Continuing.", vin);
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
                var containerName = ExtractContainerName(baseUrl) ??
                                    _configuration["BlobContainerName"] ??
                                    _configuration.GetConnectionString("BlobContainerName") ??
                                    _configuration["ConnectionStrings:BlobContainerName"];
                if (!string.IsNullOrWhiteSpace(containerName))
                {
                    return new BlobContainerClient(connString!, containerName!);
                }
            }

            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
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
                if (uri.Segments.Length <= 2) return string.Empty;
                var prefix = string.Join(string.Empty, uri.Segments.Skip(2));
                if (!string.IsNullOrEmpty(prefix) && !prefix.EndsWith("/")) prefix += "/";
                return prefix;
            }
            catch { return string.Empty; }
        }

        private static string? ExtractContainerName(string? url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) return null;
                var uri = new Uri(url);
                if (uri.Segments.Length >= 2)
                {
                    return uri.Segments[1].Trim('/');
                }
            }
            catch { }
            return null;
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

        private async Task<bool> DeleteUnit(int unitId)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = "DELETE FROM [Units] WHERE [UnitID] = @UnitID";
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@UnitID", unitId);
            var affected = await command.ExecuteNonQueryAsync();
            return affected > 0;
        }
    }
}
