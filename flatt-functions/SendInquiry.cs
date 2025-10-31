using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Azure;
using Azure.Communication.Email;
using Azure.Core;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;

namespace flatt_functions
{
    public class SendInquiry
    {
        private readonly ILogger<SendInquiry> _logger;
        private readonly IConfiguration _config;

        public SendInquiry(ILogger<SendInquiry> logger, IConfiguration config)
        {
            _logger = logger;
            _config = config;
        }

        private record TradeInInfo(
            string? Year,
            string? Make,
            string? Model,
            string? MileageOrHours,
            string? Condition,
            List<string>? Images
        );

        private record InquiryRequest(
            string? UserEmail,
            string? Subject,
            string? Message,
            string? Name,
            string? Phone,
            int? UnitId,
            string? Vin,
            Dictionary<string, object>? Meta,
            TradeInInfo? TradeIn
        );

        [Function("SendInquiry")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "inquiries")] HttpRequestData req,
            FunctionContext executionContext)
        {
            var res = req.CreateResponse();
            AddCors(res);

            try
            {
                string raw = await new StreamReader(req.Body).ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(raw))
                    return await BadRequest(res, "Empty request body.");

                var inquiry = JsonSerializer.Deserialize<InquiryRequest>(raw, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (inquiry == null || string.IsNullOrWhiteSpace(inquiry.UserEmail) || string.IsNullOrWhiteSpace(inquiry.Message))
                    return await BadRequest(res, "userEmail and message fields are required.");

                // Enqueue message for background processing
                var storageConn = Environment.GetEnvironmentVariable("StorageConnection");
                var queueUrl = Environment.GetEnvironmentVariable("InquiryQueueURL");
                var queueNameSetting = Environment.GetEnvironmentVariable("InquiryQueueName");
                string queueName = !string.IsNullOrWhiteSpace(queueUrl)
                    ? GetQueueNameFromUrl(queueUrl)
                    : (queueNameSetting ?? "inquiries");
                if (string.IsNullOrWhiteSpace(storageConn))
                    return await Error(res, "StorageConnection setting is missing.");

                var qOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
                var queueClient = new QueueClient(storageConn, queueName, qOptions);
                await queueClient.CreateIfNotExistsAsync();

                // No additional transformation here; the queue processor will build emails and branding.

                // Build a compact queue payload
                var correlationId = Guid.NewGuid().ToString("N");
                var queuePayload = new
                {
                    CorrelationId = correlationId,
                    ReceivedAtUtc = DateTime.UtcNow,
                    Inquiry = inquiry
                };
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = false,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                    ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
                };
                string payload = JsonSerializer.Serialize(queuePayload, jsonOptions);
                var sendResult = await queueClient.SendMessageAsync(payload);
                _logger.LogInformation("Enqueued inquiry: CorrelationId={cid}, Queue={queue}, MessageId={mid}", correlationId, queueName, sendResult?.Value?.MessageId);

                res.StatusCode = HttpStatusCode.Accepted;
                res.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await res.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    ok = true,
                    message = "Inquiry received. Email delivery will be processed asynchronously.",
                    id = correlationId
                }));
                return res;
            }
            catch (JsonException)
            {
                return await BadRequest(res, "Invalid JSON format.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in SendInquiry");
                return await Error(res, ex.Message);
            }
        }

        private static string GetQueueNameFromUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                // Last segment is the queue name; trim slashes
                var segs = uri.Segments;
                if (segs.Length == 0) return string.Empty;
                var name = WebUtility.UrlDecode(segs[^1]).Trim('/');
                return name;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void AddCors(HttpResponseData res)
        {
            res.Headers.Add("Access-Control-Allow-Origin", "*");
            res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            res.Headers.Add("Cache-Control", "no-store, no-cache, must-revalidate, max-age=0");
            res.Headers.Add("Pragma", "no-cache");
            res.Headers.Add("Expires", "Thu, 01 Jan 1970 00:00:00 GMT");
        }

        private static async Task<HttpResponseData> Error(HttpResponseData res, string message)
        {
            res.StatusCode = HttpStatusCode.InternalServerError;
            res.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await res.WriteStringAsync(JsonSerializer.Serialize(new { error = true, message }));
            return res;
        }

        private static async Task<HttpResponseData> BadRequest(HttpResponseData res, string message)
        {
            res.StatusCode = HttpStatusCode.BadRequest;
            res.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await res.WriteStringAsync(JsonSerializer.Serialize(new { error = true, message }));
            return res;
        }
    }
}
