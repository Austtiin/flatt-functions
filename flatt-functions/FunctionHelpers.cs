using System;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;

namespace flatt_functions
{
    /// <summary>
    /// Shared helpers to remove the boilerplate (connection-string lookup, CORS,
    /// JSON options, and response shaping) that was duplicated across every function.
    /// </summary>
    public static class FunctionHelpers
    {
        /// <summary>
        /// Cached serializer options. Reusing a single instance avoids re-allocating it
        /// per response and lets System.Text.Json cache type metadata between calls.
        /// </summary>
        public static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Resolves the SQL connection string from the three configuration shapes the app uses.
        /// Throws <see cref="InvalidOperationException"/> if none are set.
        /// </summary>
        public static string ResolveConnectionString(IConfiguration configuration)
        {
            var connectionString = configuration["SqlConnectionString"] ??
                                   configuration.GetConnectionString("SqlConnectionString") ??
                                   configuration["ConnectionStrings:SqlConnectionString"];

            if (string.IsNullOrEmpty(connectionString))
            {
                throw new InvalidOperationException("SqlConnectionString not set in configuration.");
            }

            return connectionString;
        }

        /// <summary>
        /// Resolves the public blob base URL (may include CDN host and path prefix) from the
        /// configuration shapes the app uses. Returns null when blob storage isn't configured.
        /// </summary>
        public static string? ResolveBlobBaseUrl(IConfiguration configuration)
        {
            return configuration["BlobBaseURL"] ??
                   configuration["Blob_URL"] ??
                   configuration.GetConnectionString("BlobBaseURL") ??
                   configuration.GetConnectionString("Blob_URL") ??
                   configuration["ConnectionStrings:BlobBaseURL"] ??
                   configuration["ConnectionStrings:Blob_URL"];
        }

        /// <summary>
        /// Builds the convention-based first-image thumbnail URL ({baseUrl}/{VIN}/1.webp).
        /// Mirrors the layout used by the Images functions (uploads are stored as sequential .webp).
        /// Returns null when the base URL or VIN is missing.
        /// </summary>
        public static string? BuildThumbnailUrl(string? baseUrl, string? vin)
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(vin))
                return null;

            return baseUrl!.TrimEnd('/') + "/" + vin!.Trim() + "/1.webp";
        }

        /// <summary>Adds the standard permissive CORS headers (including OPTIONS preflight support).</summary>
        public static void AddCors(HttpResponseData response, string methods = "GET, POST, OPTIONS")
        {
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", methods);
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        }

        /// <summary>Adds headers that prevent caching of write/sensitive responses.</summary>
        public static void AddNoCache(HttpResponseData response)
        {
            response.Headers.Add("Cache-Control", "no-store, no-cache, must-revalidate, max-age=0");
            response.Headers.Add("Pragma", "no-cache");
            response.Headers.Add("Expires", "Thu, 01 Jan 1970 00:00:00 GMT");
        }

        /// <summary>Serializes <paramref name="payload"/> as JSON with the given status code.</summary>
        public static async Task WriteJsonAsync(
            HttpResponseData response,
            HttpStatusCode statusCode,
            object payload,
            CancellationToken cancellationToken = default)
        {
            response.StatusCode = statusCode;
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await response.WriteStringAsync(JsonSerializer.Serialize(payload, Json), cancellationToken);
        }

        /// <summary>
        /// Writes a client-safe error envelope. The full exception should be logged separately;
        /// only a generic message and a correlation id are returned to the caller.
        /// </summary>
        public static Task WriteErrorAsync(
            HttpResponseData response,
            HttpStatusCode statusCode,
            string message,
            string? correlationId = null,
            CancellationToken cancellationToken = default)
        {
            return WriteJsonAsync(response, statusCode, new
            {
                Error = true,
                Message = message,
                CorrelationId = correlationId,
                StatusCode = (int)statusCode,
                Timestamp = DateTime.UtcNow
            }, cancellationToken);
        }
    }
}
