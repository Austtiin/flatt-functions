using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Communication.Sms;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace flatt_functions
{
    public class DispatchQueuedSms
    {
        private readonly ILogger<DispatchQueuedSms> _logger;
        private readonly string _connectionString;
        private readonly string _smsConnectionString;
        private readonly string _smsFromNumber;

        public DispatchQueuedSms(ILogger<DispatchQueuedSms> logger, IConfiguration configuration)
        {
            _logger = logger;

            var sqlConnectionString = configuration["SqlConnectionString"] ??
                                      configuration.GetConnectionString("SqlConnectionString") ??
                                      configuration["ConnectionStrings:SqlConnectionString"];

            if (string.IsNullOrWhiteSpace(sqlConnectionString))
            {
                _logger.LogError("SqlConnectionString is null or empty in configuration");
                throw new InvalidOperationException("SqlConnectionString not set in configuration.");
            }

            _connectionString = sqlConnectionString;
            _smsConnectionString = configuration["SmsConnectionString"] ?? string.Empty;
            _smsFromNumber = configuration["SmsFromNumber"] ?? string.Empty;
        }

        [Function("DispatchQueuedSms")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "sms/dispatch")] HttpRequestData req)
        {
            var response = req.CreateResponse();
            AddCors(response);

            if (string.IsNullOrWhiteSpace(_smsConnectionString) || string.IsNullOrWhiteSpace(_smsFromNumber))
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await response.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    error = true,
                    message = "SmsConnectionString and SmsFromNumber must be configured before dispatching SMS."
                }));
                return response;
            }

            int maxBatchSize = 25;
            try
            {
                var body = await req.ReadAsStringAsync();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    var payload = JsonSerializer.Deserialize<DispatchRequest>(body, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (payload?.MaxBatchSize is > 0 and <= 200)
                    {
                        maxBatchSize = payload.MaxBatchSize.Value;
                    }
                }
            }
            catch (JsonException)
            {
                return await BadRequest(response, "Invalid JSON format for dispatch request.");
            }

            int attempted = 0;
            int sent = 0;
            int failed = 0;
            var failures = new List<object>();

            var smsClient = new SmsClient(_smsConnectionString);

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                var messages = await ReadQueuedMessages(connection, maxBatchSize);
                foreach (var message in messages)
                {
                    var claimed = await MarkSendingIfQueued(connection, message.MessageId);
                    if (!claimed)
                    {
                        continue;
                    }

                    attempted++;

                    try
                    {
                        var sendResult = await smsClient.SendAsync(
                            from: _smsFromNumber,
                            to: message.PhoneNumber,
                            message: message.MessageText);

                        var success = sendResult.Value.Successful;
                        if (success)
                        {
                            sent++;
                            await UpdateDeliveryStatus(connection, message.MessageId, "Sent");
                        }
                        else
                        {
                            failed++;
                            await UpdateDeliveryStatus(connection, message.MessageId, "Failed");
                            failures.Add(new
                            {
                                messageId = message.MessageId,
                                phoneNumber = message.PhoneNumber,
                                error = "Provider returned unsuccessful result."
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        await UpdateDeliveryStatus(connection, message.MessageId, "Failed");
                        failures.Add(new
                        {
                            messageId = message.MessageId,
                            phoneNumber = message.PhoneNumber,
                            error = ex.Message
                        });
                    }
                }
            }

            response.StatusCode = HttpStatusCode.OK;
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await response.WriteStringAsync(JsonSerializer.Serialize(new
            {
                success = true,
                attempted,
                sent,
                failed,
                message = "Queued SMS dispatch run completed.",
                failures
            }));

            return response;
        }

        private static async Task<List<QueuedMessage>> ReadQueuedMessages(SqlConnection connection, int maxBatchSize)
        {
            const string sql = @"
SELECT TOP (@MaxBatchSize)
    m.[MessageId],
    m.[PhoneId],
    m.[MessageText],
    p.[PhoneNumber]
FROM [dbo].[Messages] m
INNER JOIN [dbo].[PhoneNumbers] p ON p.[PhoneId] = m.[PhoneId]
WHERE m.[Direction] = 'Outbound'
  AND m.[DeliveryStatus] = 'Queued'
ORDER BY m.[Timestamp] ASC;";

            var result = new List<QueuedMessage>();
            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@MaxBatchSize", maxBatchSize);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var messageId = reader.GetInt32(reader.GetOrdinal("MessageId"));
                var phoneId = reader.GetInt32(reader.GetOrdinal("PhoneId"));
                var messageText = reader["MessageText"] == DBNull.Value ? string.Empty : Convert.ToString(reader["MessageText"]) ?? string.Empty;
                var phoneNumber = reader["PhoneNumber"] == DBNull.Value ? string.Empty : Convert.ToString(reader["PhoneNumber"]) ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(messageText) && !string.IsNullOrWhiteSpace(phoneNumber))
                {
                    result.Add(new QueuedMessage(messageId, phoneId, phoneNumber, messageText));
                }
            }

            return result;
        }

        private static async Task<bool> MarkSendingIfQueued(SqlConnection connection, int messageId)
        {
            const string sql = @"
UPDATE [dbo].[Messages]
SET [DeliveryStatus] = 'Sending'
WHERE [MessageId] = @MessageId
  AND [DeliveryStatus] = 'Queued';";

            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@MessageId", messageId);
            var rows = await command.ExecuteNonQueryAsync();
            return rows == 1;
        }

        private static async Task UpdateDeliveryStatus(SqlConnection connection, int messageId, string status)
        {
            const string sql = @"
UPDATE [dbo].[Messages]
SET [DeliveryStatus] = @Status
WHERE [MessageId] = @MessageId;";

            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Status", status);
            command.Parameters.AddWithValue("@MessageId", messageId);
            await command.ExecuteNonQueryAsync();
        }

        private static void AddCors(HttpResponseData response)
        {
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        }

        private static async Task<HttpResponseData> BadRequest(HttpResponseData response, string message)
        {
            response.StatusCode = HttpStatusCode.BadRequest;
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await response.WriteStringAsync(JsonSerializer.Serialize(new { error = true, message }));
            return response;
        }

        private record DispatchRequest
        {
            public int? MaxBatchSize { get; set; }
        }

        private record QueuedMessage(int MessageId, int PhoneId, string PhoneNumber, string MessageText);
    }
}
