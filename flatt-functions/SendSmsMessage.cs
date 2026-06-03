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
    public class SendSmsMessage
    {
        private readonly ILogger<SendSmsMessage> _logger;
        private readonly string _connectionString;

        public SendSmsMessage(ILogger<SendSmsMessage> logger, IConfiguration configuration)
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

        [Function("SendSmsMessage")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sms/send")] HttpRequestData req)
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

                SendSmsRequest? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<SendSmsRequest>(body, new JsonSerializerOptions
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

                var trimmedLeadFrom = payload.LeadFrom?.Trim();
                if (string.IsNullOrWhiteSpace(trimmedLeadFrom))
                {
                    trimmedLeadFrom = null;
                }

                int phoneId;
                bool smsOptInConfirmed;
                string status;
                string outboundMessage;
                bool sentBusinessMessage;

                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    var phone = await EnsurePhoneRecord(connection, normalizedPhone, trimmedLeadFrom);
                    phoneId = phone.PhoneId;
                    smsOptInConfirmed = phone.SmsOptInConfirmed;
                    status = phone.Status;

                    if (!smsOptInConfirmed || !string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase))
                    {
                        outboundMessage = "Please opt in for messages from Forest Lake Auto Truck & Trailer. Reply 'YES' to confirm; data rates may apply. Reply STOP to unsubscribe.";
                        sentBusinessMessage = false;

                        if (!string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase))
                        {
                            await UpdatePhoneStatus(connection, phoneId, "Pending", optInRequested: true);
                            status = "Pending";
                        }
                    }
                    else
                    {
                        outboundMessage = payload.MessageText.Trim();
                        sentBusinessMessage = true;
                    }

                    // This stores the outbound event in the Messages table as the centralized source of SMS intent.
                    // A provider integration can pick up records with DeliveryStatus='Queued'.
                    await InsertMessage(connection, phoneId, "Outbound", outboundMessage, "Queued");
                }

                response.StatusCode = HttpStatusCode.Accepted;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await response.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    success = true,
                    phoneId,
                    phoneNumber = normalizedPhone,
                    smsOptInConfirmed,
                    status,
                    sentBusinessMessage,
                    queuedMessage = outboundMessage,
                    message = sentBusinessMessage
                        ? "Message queued for delivery."
                        : "Phone is not opted in. Opt-in prompt queued instead."
                }));

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in SendSmsMessage");
                return await ServerError(response, "An internal server error occurred.");
            }
        }

        private static async Task<(int PhoneId, bool SmsOptInConfirmed, string Status)> EnsurePhoneRecord(
            SqlConnection connection,
            string normalizedPhone,
            string? leadFrom)
        {
            const string selectExistingSql = @"
SELECT TOP 1 [PhoneId], [SmsOptInConfirmed], [Status]
FROM [dbo].[PhoneNumbers]
WHERE [PhoneNumber] = @PhoneNumber
ORDER BY [PhoneId] DESC;";

            using (var selectCommand = new SqlCommand(selectExistingSql, connection))
            {
                selectCommand.Parameters.AddWithValue("@PhoneNumber", normalizedPhone);
                using var reader = await selectCommand.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var phoneId = reader.GetInt32(reader.GetOrdinal("PhoneId"));
                    var confirmed = reader["SmsOptInConfirmed"] != DBNull.Value && Convert.ToBoolean(reader["SmsOptInConfirmed"]);
                    var status = reader["Status"] == DBNull.Value ? "Pending" : Convert.ToString(reader["Status"]) ?? "Pending";
                    return (phoneId, confirmed, status);
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
            insertCommand.Parameters.AddWithValue("@SmsOptInRequested", true);
            insertCommand.Parameters.AddWithValue("@SmsOptInConfirmed", false);
            insertCommand.Parameters.AddWithValue("@OptInRequestedDate", DateTime.UtcNow);
            insertCommand.Parameters.AddWithValue("@OptInConfirmedDate", DBNull.Value);
            insertCommand.Parameters.AddWithValue("@Status", "Pending");
            insertCommand.Parameters.AddWithValue("@LeadFrom", (object?)leadFrom ?? DBNull.Value);

            var result = await insertCommand.ExecuteScalarAsync();
            var createdPhoneId = result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
            if (createdPhoneId <= 0)
            {
                throw new InvalidOperationException("Failed to create phone record.");
            }

            return (createdPhoneId, false, "Pending");
        }

        private static async Task UpdatePhoneStatus(SqlConnection connection, int phoneId, string status, bool optInRequested)
        {
            const string updateSql = @"
UPDATE [dbo].[PhoneNumbers]
SET
    [Status] = @Status,
    [SmsOptInRequested] = @SmsOptInRequested,
    [OptInRequestedDate] = CASE WHEN @SmsOptInRequested = 1 THEN @NowUtc ELSE [OptInRequestedDate] END
WHERE [PhoneId] = @PhoneId;";

            using var updateCommand = new SqlCommand(updateSql, connection);
            updateCommand.Parameters.AddWithValue("@Status", status);
            updateCommand.Parameters.AddWithValue("@SmsOptInRequested", optInRequested);
            updateCommand.Parameters.AddWithValue("@NowUtc", DateTime.UtcNow);
            updateCommand.Parameters.AddWithValue("@PhoneId", phoneId);
            await updateCommand.ExecuteNonQueryAsync();
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

        private class SendSmsRequest
        {
            public string? PhoneNumber { get; set; }
            public string? MessageText { get; set; }
            public string? LeadFrom { get; set; }
        }
    }
}
