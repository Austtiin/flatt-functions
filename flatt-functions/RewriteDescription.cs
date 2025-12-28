using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace flatt_functions
{
    public class RewriteDescription
    {
        private readonly ILogger<RewriteDescription> _logger;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;

        public RewriteDescription(ILogger<RewriteDescription> logger, IConfiguration configuration, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
        }

        public class RewriteRequest
        {
            [JsonPropertyName("description")] public string? Description { get; set; }
            [JsonPropertyName("tone")] public string? Tone { get; set; } // professional|casual|neutral|sales
            [JsonPropertyName("maxWords")] public int? MaxWords { get; set; } // default 120
            [JsonPropertyName("previewOnly")] public bool? PreviewOnly { get; set; } // if true, don't call AI
        }

        public class Stage
        {
            public string Status { get; set; } = string.Empty; // received|loading|complete|error
            public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
        }

        public class RewriteResponse
        {
            public string Status { get; set; } = "complete";
            public List<Stage> Stages { get; set; } = new();
            public string? PromptBuilt { get; set; }
            public string? RewrittenText { get; set; }
            public object Meta { get; set; } = new();
            public object? Error { get; set; }
        }

        [Function("RewriteDescription")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ai/rewrite")] HttpRequestData req)
        {
            var response = req.CreateResponse();
            AddCors(response);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");

            var stages = new List<Stage> { new Stage { Status = "received", At = DateTimeOffset.UtcNow } };

            try
            {
                // Read body
                var body = await new System.IO.StreamReader(req.Body).ReadToEndAsync();
                _logger.LogInformation("[RewriteDescription] Request body received: {bodyLength} bytes", body?.Length ?? 0);
                
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var input = JsonSerializer.Deserialize<RewriteRequest>(body, options) ?? new RewriteRequest();

                _logger.LogInformation("[RewriteDescription] Request parsed - PreviewOnly: {previewOnly}, Description length: {len}, Tone: {tone}, MaxWords: {maxWords}", 
                    input.PreviewOnly == true, 
                    input.Description?.Length ?? 0,
                    input.Tone ?? "not specified",
                    input.MaxWords ?? 0);

                // Validate input
                if (string.IsNullOrWhiteSpace(input.Description))
                {
                    _logger.LogWarning("[RewriteDescription] Validation failed: Description is required");
                    response.StatusCode = HttpStatusCode.BadRequest;
                    var error = new RewriteResponse
                    {
                        Status = "error",
                        Stages = stages,
                        Error = new { message = "Field 'description' is required." }
                    };
                    await response.WriteStringAsync(JsonSerializer.Serialize(error, JsonOptions()));
                    return response;
                }

                // Normalize options
                var tone = NormalizeTone(input.Tone);
                var maxWords = input.MaxWords.HasValue && input.MaxWords.Value > 10 ? input.MaxWords.Value : 120;

                _logger.LogInformation("[RewriteDescription] Normalized options - Tone: {tone}, MaxWords: {maxWords}", tone, maxWords);

                // Build prompt header for preview/editing
                var promptHeader = BuildPromptHeader(tone, maxWords);
                var promptBuilt = $"{promptHeader}\n\nDescription:\n{input.Description}";

                _logger.LogInformation("[RewriteDescription] Prompt built - Length: {len}, Sample: {sample}", promptBuilt.Length, TruncateForLog(promptBuilt, 240));

                // Prepare base response
                var baseResp = new RewriteResponse
                {
                    Status = "loading",
                    Stages = stages,
                    PromptBuilt = promptBuilt,
                    Meta = new { tone, maxWords }
                };

                // If preview-only, return without calling AI
                if (input.PreviewOnly == true)
                {
                    _logger.LogInformation("[RewriteDescription] Preview-only mode, skipping AI call");
                    stages.Add(new Stage { Status = "complete", At = DateTimeOffset.UtcNow });
                    baseResp.Status = "complete";
                    response.StatusCode = HttpStatusCode.OK;
                    await response.WriteStringAsync(JsonSerializer.Serialize(baseResp, JsonOptions()));
                    return response;
                }

                // Load config for Azure OpenAI
                // Priority: local.settings.json Values -> ConnectionStrings (for production)
                var endpoint = _configuration["openAIEndpoint"] ?? _configuration["ConnectionStrings:openAIEndpoint"];
                var key = _configuration["openAIkey"] ?? _configuration["openAIKey"] ?? _configuration["ConnectionStrings:openAIkey"] ?? _configuration["ConnectionStrings:openAIKey"];
                var deployment = _configuration["AIDeploymentName"]
                    ?? _configuration["openAIDeployment"]
                    ?? _configuration["ConnectionStrings:AIDeploymentName"]
                    ?? _configuration["ConnectionStrings:openAIDeployment"];
                var apiVersion = _configuration["openAIApiVersion"] ?? _configuration["ConnectionStrings:openAIApiVersion"];

                _logger.LogInformation("[RewriteDescription] Configuration loaded - Endpoint: {endpoint}, Key: {keyPresent}, Deployment: {deployment}, ApiVersion: {apiVersion}",
                    endpoint ?? "NOT SET",
                    !string.IsNullOrWhiteSpace(key) ? "***" + key.Substring(Math.Max(0, key.Length - 4)) : "NOT SET",
                    deployment ?? "NOT SET",
                    apiVersion ?? "NOT SET");

                if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key))
                {
                    _logger.LogError("[RewriteDescription] Missing configuration - Endpoint present: {endpointPresent}, Key present: {keyPresent}",
                        !string.IsNullOrWhiteSpace(endpoint),
                        !string.IsNullOrWhiteSpace(key));
                    response.StatusCode = HttpStatusCode.InternalServerError;
                    var err = new RewriteResponse
                    {
                        Status = "error",
                        Stages = stages,
                        PromptBuilt = promptBuilt,
                        Error = new { message = "Azure OpenAI configuration missing: 'openAIEndpoint' and/or 'openAIkey'." }
                    };
                    await response.WriteStringAsync(JsonSerializer.Serialize(err, JsonOptions()));
                    return response;
                }

                if (string.IsNullOrWhiteSpace(deployment))
                {
                    _logger.LogError("[RewriteDescription] Deployment name is missing from configuration");
                    response.StatusCode = HttpStatusCode.BadRequest;
                    var err = new RewriteResponse
                    {
                        Status = "error",
                        Stages = stages,
                        PromptBuilt = promptBuilt,
                        Error = new { message = "Azure OpenAI deployment ID missing. Set 'AIDeploymentName' (or 'openAIDeployment') in settings." }
                    };
                    await response.WriteStringAsync(JsonSerializer.Serialize(err, JsonOptions()));
                    return response;
                }

                stages.Add(new Stage { Status = "loading", At = DateTimeOffset.UtcNow });

                string? rewritten = null;
                try
                {
                    // Always use the configured deployment name from settings
                    var modelToUse = deployment;
                    _logger.LogInformation("[RewriteDescription] Using configured deployment: {deployment}", modelToUse);
                    
                    // Derive base endpoint for SDK from configured endpoint
                    // Azure OpenAI endpoint should be: https://{resource-name}.openai.azure.com/ or https://{resource-name}.cognitiveservices.azure.com/
                    // NOT the full path with /openai/responses
                    Uri baseUri;
                    try
                    {
                        var u = new Uri(endpoint);
                        var port = u.IsDefaultPort ? string.Empty : $":{u.Port}";
                        // Extract just the base URL (scheme + host + port)
                        baseUri = new Uri($"{u.Scheme}://{u.Host}{port}/");
                        _logger.LogInformation("[RewriteDescription] Parsed endpoint URI - Original: {original}, Base: {base}", endpoint, baseUri);
                    }
                    catch (Exception parseEx)
                    {
                        _logger.LogWarning(parseEx, "[RewriteDescription] Failed to parse endpoint as URI, attempting fallback parsing");
                        baseUri = new Uri("https://" + endpoint.Trim().Replace("https://", string.Empty).Replace("http://", string.Empty).Split('/')[0] + "/");
                        _logger.LogInformation("[RewriteDescription] Fallback endpoint: {base}", baseUri);
                    }

                    _logger.LogInformation("[RewriteDescription] Preparing Azure OpenAI call - Base URI: {base}, Model/Deployment: {model}, Key length: {keyLen}", 
                        baseUri, modelToUse, key?.Length ?? 0);

                    _logger.LogInformation("[RewriteDescription] Creating AzureOpenAIClient with base URI: {uri}", baseUri);
                    var azureClient = new AzureOpenAIClient(baseUri, new AzureKeyCredential(key));
                    
                    _logger.LogInformation("[RewriteDescription] Getting ChatClient for model: {model}", modelToUse);
                    var chatClient = azureClient.GetChatClient(modelToUse);

                    var messages = new List<ChatMessage>
                    {
                        new SystemChatMessage(promptHeader),
                        new UserChatMessage($"Description:\n{input.Description}")
                    };
                    
                    _logger.LogInformation("[RewriteDescription] Chat messages prepared - System message length: {sysLen}, User message length: {userLen}",
                        promptHeader.Length, input.Description.Length);

                    // Check if stored completions should be enabled
                    var enableStored =
                        string.Equals(_configuration["AIStoreCompletions"], "true", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(_configuration["ConnectionStrings:AIStoreCompletions"], "true", StringComparison.OrdinalIgnoreCase);

                    if (enableStored)
                    {
                        // Use REST API to enable stored completions (store=true)
                        _logger.LogInformation("[RewriteDescription] Using REST API with store=true for deployment: {deployment}", modelToUse);
                        
                        var http = _httpClientFactory.CreateClient();
                        http.BaseAddress = baseUri;
                        http.DefaultRequestHeaders.Remove("api-key");
                        http.DefaultRequestHeaders.Add("api-key", key);

                        var payload = new
                        {
                            model = modelToUse,
                            store = true,
                            messages = new object[]
                            {
                                new { role = "system", content = promptHeader },
                                new { role = "user", content = $"Description:\n{input.Description}" }
                            },
                            metadata = new Dictionary<string, string>
                            {
                                ["feature"] = "rewriteDescription",
                                ["tone"] = tone,
                                ["maxWords"] = maxWords.ToString()
                            }
                        };

                        var json = JsonSerializer.Serialize(payload);
                        _logger.LogInformation("[RewriteDescription] Request payload prepared - Size: {size} bytes, store: true", json.Length);
                        
                        using var content = new StringContent(json, Encoding.UTF8, "application/json");
                        var restUrl = $"openai/v1/chat/completions";
                        _logger.LogInformation("[RewriteDescription] Sending POST to: {url}", restUrl);
                        
                        var resp = await http.PostAsync(restUrl, content);
                        var bodyText = await resp.Content.ReadAsStringAsync();
                        
                        _logger.LogInformation("[RewriteDescription] Response received - Status: {status} ({statusCode}), Body length: {bodyLen}",
                            resp.StatusCode, (int)resp.StatusCode, bodyText?.Length ?? 0);

                        if (resp.IsSuccessStatusCode)
                        {
                            using var doc = JsonDocument.Parse(bodyText);
                            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                            {
                                var choice0 = choices[0];
                                if (choice0.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var contentEl))
                                {
                                    rewritten = contentEl.GetString();
                                }
                            }
                            _logger.LogInformation("[RewriteDescription] ✓ REST API completion succeeded with store=true - Content length: {len}", rewritten?.Length ?? 0);
                        }
                        else
                        {
                            throw new Exception($"REST API call failed: {(int)resp.StatusCode} {resp.StatusCode}. Body: {bodyText}");
                        }
                    }
                    else
                    {
                        // Use SDK approach (simpler, no stored completions)
                        _logger.LogInformation("[RewriteDescription] Using SDK ChatClient.CompleteChat for deployment: {deployment}", modelToUse);
                        
                        var optionsChat = new ChatCompletionOptions();
                        var chatResp = chatClient.CompleteChat(messages, optionsChat);
                        _logger.LogInformation("[RewriteDescription] SDK response received - Content parts: {count}", chatResp.Value.Content.Count);
                        
                        if (chatResp.Value.Content.Count > 0)
                        {
                            rewritten = chatResp.Value.Content[0].Text;
                            _logger.LogInformation("[RewriteDescription] ✓ SDK chat completion succeeded - Content length: {len}", rewritten?.Length ?? 0);
                        }
                        else
                        {
                            _logger.LogWarning("[RewriteDescription] SDK returned no content in response");
                        }
                    }
                }
                catch (Exception sdkEx)
                {
                    _logger.LogError(sdkEx, "[RewriteDescription] ✗ Azure OpenAI call failed - Exception type: {type}, Message: {message}, StackTrace: {stack}",
                        sdkEx.GetType().Name, sdkEx.Message, sdkEx.StackTrace);
                    response.StatusCode = HttpStatusCode.BadGateway;
                    var err = new RewriteResponse
                    {
                        Status = "error",
                        Stages = stages,
                        PromptBuilt = promptBuilt,
                        Error = new { message = "Azure OpenAI SDK chat call failed", details = sdkEx.Message, type = sdkEx.GetType().Name }
                    };
                    await response.WriteStringAsync(JsonSerializer.Serialize(err, JsonOptions()));
                    return response;
                }

                if (string.IsNullOrEmpty(rewritten))
                {
                    _logger.LogWarning("[RewriteDescription] Rewritten text is empty or null");
                }
                else
                {
                    _logger.LogInformation("[RewriteDescription] ✓ Rewrite complete - Length: {len}, Sample: {sample}", 
                        rewritten.Length, TruncateForLog(rewritten, 240));
                }

                stages.Add(new Stage { Status = "complete", At = DateTimeOffset.UtcNow });

                var ok = new RewriteResponse
                {
                    Status = "complete",
                    Stages = stages,
                    PromptBuilt = promptBuilt,
                    RewrittenText = rewritten,
                    Meta = new { tone, maxWords, deployment }
                };

                response.StatusCode = HttpStatusCode.OK;
                var responseJson = JsonSerializer.Serialize(ok, JsonOptions());
                _logger.LogInformation("[RewriteDescription] ✓ Sending success response - Size: {size} bytes", responseJson.Length);
                await response.WriteStringAsync(responseJson);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RewriteDescription] ✗ Unexpected error - Type: {type}, Message: {message}, StackTrace: {stack}",
                    ex.GetType().Name, ex.Message, ex.StackTrace);
                var err = new RewriteResponse
                {
                    Status = "error",
                    Stages = stages,
                    Error = new { message = "Unexpected error", details = ex.Message, type = ex.GetType().Name }
                };
                response.StatusCode = HttpStatusCode.InternalServerError;
                await response.WriteStringAsync(JsonSerializer.Serialize(err, JsonOptions()));
                return response;
            }
        }

        private static void AddCors(HttpResponseData response)
        {
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        }

        private static string NormalizeTone(string? tone)
        {
            if (string.IsNullOrWhiteSpace(tone)) return "professional";
            var t = tone.Trim().ToLowerInvariant();
            return t switch
            {
                "professional" => "professional",
                "casual" => "casual",
                "neutral" => "neutral",
                "sales" => "sales",
                _ => "professional"
            };
        }

        private static string BuildPromptHeader(string tone, int maxWords)
        {
            // Fixed header per frontend expectations
            return $@"Rewrite the following inventory description to be:
            - Clear and professional
            - Sales-focused but persuasive
            - Engaging and concise
            - Easy for a customer to understand
            - No added features or made up information
            - No jargon or NSFW language
            - Written in a {tone} tone
            - Paragraphs only, no bullet points
            - Under {maxWords} words";
        }

        private static JsonSerializerOptions JsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            };
        }

        private static string TruncateForLog(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (text.Length <= max) return text;
            return text.Substring(0, max) + "...";
        }
    }
}
