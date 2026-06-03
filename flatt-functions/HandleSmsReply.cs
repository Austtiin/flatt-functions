using System;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace flatt_functions
{
    public class HandleSmsReply
    {
        private readonly ILogger<HandleSmsReply> _logger;
        private readonly string _connectionString;

        public HandleSmsReply(ILogger<HandleSmsReply> logger, IConfiguration configuration)
        {
            _logger = logger;

            var connectionString = configuration["SqlConnectionString"] ??
                                   configuration.GetConnectionString("SqlConnectionString") ??
                                   configuration["ConnectionStrings:SqlConnectionString"];

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logger.LogError("SqlConnectionString is null or empty in configuration");
                throw new InvalidOperationException("SqlConnectionString not set in configuration.");
            }

            _connectionString = connectionString;
        }

        [Function("HandleSmsReply")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sms/reply")] HttpRequestData req)
        {
            var response = req.CreateResponse();
            AddCors(response);

            try
            {
                var body = await req.ReadAsStringAsync() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(body))
                {
                    return await BadRequest(response, "Request body is required.");
                }

                SmsReplyRequest? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<SmsReplyRequest>(body, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
                catch (JsonException)
                {
                    return await BadRequest(response, "Invalid JSON format.");
                }

                if (payload == null)
                {
                    return await BadRequest(response, "Request body is required.");
                }

                if (string.IsNullOrWhiteSpace(payload.PhoneNumber))
                {
                    return await BadRequest(response, "phoneNumber is required.");
                }

                if (string.IsNullOrWhiteSpace(payload.MessageText))
                {
                    return await BadRequest(response, "messageText is required.");
                }

                var normalizedPhone = NormalizePhoneNumber(payload.PhoneNumber);
                if (string.IsNullOrWhiteSpace(normalizedPhone))
                {
                    return await BadRequest(response, "phoneNumber is invalid.");
                }

                var inboundText = payload.MessageText.Trim();
                var normalizedReply = inboundText.ToUpperInvariant();

                int phoneId;
                string status;
                bool smsOptInConfirmed;
                string? outboundAck = null;

                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    phoneId = await EnsurePhoneRecord(connection, normalizedPhone);

                    await InsertMessage(connection, phoneId, "Inbound", inboundText, "Received");

                    if (normalizedReply == "YES" || normalizedReply == "Y")
                    {
                        status = "Active";
                        smsOptInConfirmed = true;
                        outboundAck = "You are subscribed to SMS alerts from Flatt's. Reply STOP to unsubscribe.";
                        await UpdateOptInState(connection, phoneId, status, smsOptInConfirmed);
                        await InsertMessage(connection, phoneId, "Outbound", outboundAck, "Queued");
                    }
                    else if (normalizedReply == "STOP" || normalizedReply == "UNSUBSCRIBE" || normalizedReply == "CANCEL" || normalizedReply == "END" || normalizedReply == "QUIT")
                    {
                        status = "Stopped";
                        smsOptInConfirmed = false;
                        outboundAck = "You are unsubscribed from SMS alerts from Flatt's. Reply YES to re-subscribe.";
                        await UpdateOptInState(connection, phoneId, status, smsOptInConfirmed);
                        await InsertMessage(connection, phoneId, "Outbound", outboundAck, "Queued");
                    }
                    else
                    {
                        var current = await ReadCurrentState(connection, phoneId);
                        status = current.Status;
                        smsOptInConfirmed = current.SmsOptInConfirmed;
                    }
                }

                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await response.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    success = true,
                    phoneId,
                    phoneNumber = normalizedPhone,
                    smsOptInConfirmed,
                    status,
                    acknowledgementQueued = outboundAck != null,
                    acknowledgementMessage = outboundAck,
                    message = "Inbound SMS processed."
                }));

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in HandleSmsReply");
                return await ServerError(response, "An internal server error occurred.");
            }
        }

        private static async Task<int> EnsurePhoneRecord(SqlConnection connection, string normalizedPhone)
        {
            const string selectSql = @"
SELECT TOP 1 [PhoneId]
FROM [dbo].[PhoneNumbers]
WHERE [PhoneNumber] = @PhoneNumber
ORDER BY [PhoneId] DESC;";

            using (var selectCommand = new SqlCommand(selectSql, connection))
            {
                selectCommand.Parameters.AddWithValue("@PhoneNumber", normalizedPhone);
                var result = await selectCommand.ExecuteScalarAsync();
                if (result != null && result != DBNull.Value)
                {
                    return Convert.ToInt32(result);
                }
            }

            const string insertSql = @"
INSERT INTO [dbo].[PhoneNumbers]
    ([PhoneNumber], [SmsOptInRequested], [SmsOptInConfirmed], [OptInRequestedDate], [OptInConfirmedDate], [Status], [LeadFrom])
VALUES
    (@PhoneNumber, @SmsOptInRequested, @SmsOptInConfirmed, @OptInRequestedDate, @OptInConfirmedDate, @Status, @LeadFrom);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

            using var insertCommand = new SqlCommand(insertSql, connection);
            insertCommand.Parameters.AddWithValue("@PhoneNumber", normalizedPhone);
            insertCommand.Parameters.AddWithValue("@SmsOptInRequested", false);
            insertCommand.Parameters.AddWithValue("@SmsOptInConfirmed", false);
            insertCommand.Parameters.AddWithValue("@OptInRequestedDate", DBNull.Value);
            insertCommand.Parameters.AddWithValue("@OptInConfirmedDate", DBNull.Value);
            insertCommand.Parameters.AddWithValue("@Status", "Pending");
            insertCommand.Parameters.AddWithValue("@LeadFrom", DBNull.Value);

            var createdResult = await insertCommand.ExecuteScalarAsync();
            var phoneId = createdResult == null || createdResult == DBNull.Value ? 0 : Convert.ToInt32(createdResult);
            if (phoneId <= 0)
            {
                throw new InvalidOperationException("Failed to create phone record for inbound SMS.");
            }

            return phoneId;
        }

        private static async Task UpdateOptInState(SqlConnection connection, int phoneId, string status, bool confirmed)
        {
            const string updateSql = @"
UPDATE [dbo].[PhoneNumbers]
SET
    [SmsOptInRequested] = CASE WHEN @Confirmed = 1 THEN 1 ELSE [SmsOptInRequested] END,
    [SmsOptInConfirmed] = @Confirmed,
    [OptInConfirmedDate] = CASE WHEN @Confirmed = 1 THEN @NowUtc ELSE NULL END,
    [Status] = @Status
WHERE [PhoneId] = @PhoneId;";

            using var updateCommand = new SqlCommand(updateSql, connection);
            updateCommand.Parameters.AddWithValue("@Confirmed", confirmed);
            updateCommand.Parameters.AddWithValue("@NowUtc", DateTime.UtcNow);
            updateCommand.Parameters.AddWithValue("@Status", status);
            updateCommand.Parameters.AddWithValue("@PhoneId", phoneId);
            await updateCommand.ExecuteNonQueryAsync();
        }

        private static async Task<(bool SmsOptInConfirmed, string Status)> ReadCurrentState(SqlConnection connection, int phoneId)
        {
            const string selectSql = @"
SELECT TOP 1 [SmsOptInConfirmed], [Status]
FROM [dbo].[PhoneNumbers]
WHERE [PhoneId] = @PhoneId;";

            using var selectCommand = new SqlCommand(selectSql, connection);
            selectCommand.Parameters.AddWithValue("@PhoneId", phoneId);
            using var reader = await selectCommand.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var confirmed = reader["SmsOptInConfirmed"] != DBNull.Value && Convert.ToBoolean(reader["SmsOptInConfirmed"]);
                var status = reader["Status"] == DBNull.Value ? "Pending" : Convert.ToString(reader["Status"]) ?? "Pending";
                return (confirmed, status);
            }

            return (false, "Pending");
        }

        private static async Task InsertMessage(
            SqlConnection connection,
            int phoneId,
            string direction,
            string messageText,
            string deliveryStatus)
        {
            const string insertMessageSql = @"
INSERT INTO [dbo].[Messages]
    ([PhoneId], [Direction], [MessageText], [Timestamp], [DeliveryStatus])
VALUES
    (@PhoneId, @Direction, @MessageText, @Timestamp, @DeliveryStatus);";

            using var insertMessageCommand = new SqlCommand(insertMessageSql, connection);
            insertMessageCommand.Parameters.AddWithValue("@PhoneId", phoneId);
            insertMessageCommand.Parameters.AddWithValue("@Direction", direction);
            insertMessageCommand.Parameters.AddWithValue("@MessageText", messageText);
            insertMessageCommand.Parameters.AddWithValue("@Timestamp", DateTime.UtcNow);
            insertMessageCommand.Parameters.AddWithValue("@DeliveryStatus", deliveryStatus);
            await insertMessageCommand.ExecuteNonQueryAsync();
        }

        private static string NormalizePhoneNumber(string? phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
            {
                return string.Empty;
            }

            var digits = Regex.Replace(phoneNumber, "[^0-9]", string.Empty);
            if (digits.Length == 11 && digits.StartsWith("1", StringComparison.Ordinal))
            {
                digits = digits.Substring(1);
            }

            return digits;
        }

        private static void AddCors(HttpResponseData response)
        {
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            response.Headers.Add("Cache-Control", "no-store, no-cache, must-revalidate, max-age=0");
            response.Headers.Add("Pragma", "no-cache");
            response.Headers.Add("Expires", "Thu, 01 Jan 1970 00:00:00 GMT");
        }

        private static async Task<HttpResponseData> BadRequest(HttpResponseData response, string message)
        {
            response.StatusCode = HttpStatusCode.BadRequest;
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await response.WriteStringAsync(JsonSerializer.Serialize(new { error = true, message }));
            return response;
        }

        private static async Task<HttpResponseData> ServerError(HttpResponseData response, string message)
        {
            response.StatusCode = HttpStatusCode.InternalServerError;
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await response.WriteStringAsync(JsonSerializer.Serialize(new { error = true, message }));
            return response;
        }

        private class SmsReplyRequest
        {
            public string? PhoneNumber { get; set; }
            public string? MessageText { get; set; }
        }
    }
}
