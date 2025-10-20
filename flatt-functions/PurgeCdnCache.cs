using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using System;
using System.Diagnostics;
using System.Text;

namespace flatt_functions
{
    public class PurgeCdnCache
    {
        private readonly ILogger<PurgeCdnCache> _logger;
        private readonly IConfiguration _configuration;
        private readonly HttpClient _httpClient;

        public PurgeCdnCache(ILogger<PurgeCdnCache> logger, IConfiguration configuration, HttpClient httpClient)
        {
            _logger = logger;
            _configuration = configuration;
            _httpClient = httpClient;
        }

        [Function("PurgeCdnCache")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "cdn/purge")] HttpRequestData req)
        {
            var stopwatch = Stopwatch.StartNew();
            var response = req.CreateResponse();

            try
            {
                _logger.LogInformation("🧹 PurgeCdnCache started");

                // CORS headers
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                // Get environment variables
                var subscriptionId = _configuration["AZURE_SUBSCRIPTION_ID"];
                var resourceGroup = _configuration["AZURE_RESOURCE_GROUP"];
                var frontDoorProfile = _configuration["AZURE_FD_PROFILE"];
                var frontDoorEndpoint = _configuration["AZURE_FD_ENDPOINT"];

                // Validate configuration 
                var missingConfigs = new List<string>();
                if (string.IsNullOrEmpty(subscriptionId)) missingConfigs.Add("AZURE_SUBSCRIPTION_ID");
                if (string.IsNullOrEmpty(resourceGroup)) missingConfigs.Add("AZURE_RESOURCE_GROUP");
                if (string.IsNullOrEmpty(frontDoorProfile)) missingConfigs.Add("AZURE_FD_PROFILE");
                if (string.IsNullOrEmpty(frontDoorEndpoint)) missingConfigs.Add("AZURE_FD_ENDPOINT");

                if (missingConfigs.Count > 0)
                {
                    _logger.LogError("❌ Missing environment variables: {missing}", string.Join(", ", missingConfigs));
                    response.StatusCode = HttpStatusCode.InternalServerError;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");

                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        Error = true,
                        Message = "Azure Front Door configuration is incomplete",
                        MissingVariables = missingConfigs,
                        RequiredVariables = new[] { "AZURE_SUBSCRIPTION_ID", "AZURE_RESOURCE_GROUP", "AZURE_FD_PROFILE", "AZURE_FD_ENDPOINT" },
                        StatusCode = 500
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

                    return response;
                }

                // Parse request body
                string requestBody = await req.ReadAsStringAsync() ?? "";
                if (string.IsNullOrWhiteSpace(requestBody))
                {
                    _logger.LogWarning("⚠️ Empty request body");
                    response.StatusCode = HttpStatusCode.BadRequest;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");

                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        Error = true,
                        Message = "Request body is required with 'contentPaths' field",
                        ExpectedFormat = new
                        {
                            contentPaths = new[] { "/*", "/images/*", "/css/style.css" },
                            domains = new[] { "www.example.com" } // optional
                        },
                        StatusCode = 400
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

                    return response;
                }

                PurgeCdnRequest? purgeRequest;
                try
                {
                    purgeRequest = JsonSerializer.Deserialize<PurgeCdnRequest>(requestBody, new JsonSerializerOptions
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

                if (purgeRequest == null || purgeRequest.ContentPaths == null || purgeRequest.ContentPaths.Length == 0)
                {
                    _logger.LogWarning("⚠️ Missing or empty contentPaths field");
                    response.StatusCode = HttpStatusCode.BadRequest;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");

                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        Error = true,
                        Message = "contentPaths field is required with at least one path",
                        Examples = new[] { "/*", "/images/*", "/css/style.css", "/api/data.json" },
                        StatusCode = 400
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

                    return response;
                }

                _logger.LogInformation("🧹 Purging {pathCount} content paths from Azure Front Door", purgeRequest.ContentPaths.Length);

                // Get access token for Azure Management API
                var accessToken = await GetAccessTokenAsync();
                if (string.IsNullOrEmpty(accessToken))
                {
                    _logger.LogError("❌ Failed to obtain Azure access token");
                    response.StatusCode = HttpStatusCode.InternalServerError;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");

                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        Error = true,
                        Message = "Failed to authenticate with Azure. Check managed identity or service principal configuration.",
                        StatusCode = 500
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

                    return response;
                }

                // Call Azure Front Door purge API
                var purgeResult = await CallAzureFrontDoorPurgeAsync(
                    subscriptionId!,
                    resourceGroup!,
                    frontDoorProfile!,
                    frontDoorEndpoint!,
                    purgeRequest,
                    accessToken);

                stopwatch.Stop();

                if (purgeResult.Success)
                {
                    _logger.LogInformation("✅ CDN cache purge initiated successfully");
                    response.StatusCode = HttpStatusCode.OK;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");

                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        Success = true,
                        Message = "CDN cache purge initiated successfully",
                        ContentPaths = purgeRequest.ContentPaths,
                        Domains = purgeRequest.Domains ?? Array.Empty<string>(),
                        SubscriptionId = subscriptionId,
                        ResourceGroup = resourceGroup,
                        FrontDoorProfile = frontDoorProfile,
                        FrontDoorEndpoint = frontDoorEndpoint,
                        Note = "Cache purging can take up to 10 minutes to propagate across all edge locations",
                        ResponseTimeMs = stopwatch.ElapsedMilliseconds,
                        Timestamp = DateTime.UtcNow
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

                    return response;
                }
                else
                {
                    _logger.LogError("❌ CDN cache purge failed: {error}", purgeResult.ErrorMessage);
                    response.StatusCode = HttpStatusCode.BadGateway;
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");

                    await response.WriteStringAsync(JsonSerializer.Serialize(new
                    {
                        Error = true,
                        Message = "CDN cache purge failed",
                        Details = purgeResult.ErrorMessage,
                        StatusCode = 502,
                        ResponseTimeMs = stopwatch.ElapsedMilliseconds,
                        Timestamp = DateTime.UtcNow
                    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

                    return response;
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "❌ Error in PurgeCdnCache - {message}", ex.Message);
                response.StatusCode = HttpStatusCode.InternalServerError;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");

                await response.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    Error = true,
                    Message = "An internal server error occurred while purging CDN cache",
                    Details = ex.Message,
                    StatusCode = 500,
                    ResponseTimeMs = stopwatch.ElapsedMilliseconds,
                    Timestamp = DateTime.UtcNow
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));

                return response;
            }
        }

        private async Task<string?> GetAccessTokenAsync()
        {
            try
            {
                // Use Azure.Identity DefaultAzureCredential for authentication
                // This works with Managed Identity in Azure Functions
                var credential = new Azure.Identity.DefaultAzureCredential();
                var tokenRequestContext = new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" });
                var token = await credential.GetTokenAsync(tokenRequestContext);
                return token.Token;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to get Azure access token: {message}", ex.Message);
                return null;
            }
        }

        private async Task<PurgeResult> CallAzureFrontDoorPurgeAsync(
            string subscriptionId,
            string resourceGroup,
            string profileName,
            string endpointName,
            PurgeCdnRequest purgeRequest,
            string accessToken)
        {
            try
            {
                // Azure Front Door purge endpoint
                var purgeUrl = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Cdn/profiles/{profileName}/afdEndpoints/{endpointName}/purge?api-version=2025-06-01";

                // Prepare request body for Azure API
                var azurePurgeBody = new
                {
                    contentPaths = purgeRequest.ContentPaths,
                    domains = purgeRequest.Domains ?? Array.Empty<string>()
                };

                var jsonBody = JsonSerializer.Serialize(azurePurgeBody, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                _logger.LogInformation("📡 Calling Azure Front Door purge API: {url}", purgeUrl);
                _logger.LogInformation("📡 Request body: {body}", jsonBody);

                using var request = new HttpRequestMessage(HttpMethod.Post, purgeUrl);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                var apiResponse = await _httpClient.SendAsync(request);

                if (apiResponse.IsSuccessStatusCode)
                {
                    _logger.LogInformation("✅ Azure Front Door purge API call succeeded: {statusCode}", apiResponse.StatusCode);
                    return new PurgeResult { Success = true };
                }
                else
                {
                    var errorContent = await apiResponse.Content.ReadAsStringAsync();
                    _logger.LogError("❌ Azure Front Door purge API call failed: {statusCode} - {error}", apiResponse.StatusCode, errorContent);
                    return new PurgeResult 
                    { 
                        Success = false, 
                        ErrorMessage = $"Azure API returned {apiResponse.StatusCode}: {errorContent}" 
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Exception calling Azure Front Door purge API: {message}", ex.Message);
                return new PurgeResult 
                { 
                    Success = false, 
                    ErrorMessage = $"Exception: {ex.Message}" 
                };
            }
        }
    }

    public class PurgeCdnRequest
    {
        public string[] ContentPaths { get; set; } = Array.Empty<string>();
        public string[]? Domains { get; set; }
    }

    public class PurgeResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }
}