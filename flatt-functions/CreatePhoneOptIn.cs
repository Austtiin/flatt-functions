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
    public class CreatePhoneOptIn
    {
        private readonly ILogger<CreatePhoneOptIn> _logger;
        private readonly string _connectionString;

        public CreatePhoneOptIn(ILogger<CreatePhoneOptIn> logger, IConfiguration configuration)
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

        [Function("CreatePhoneOptIn")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sms/opt-in")] HttpRequestData req)
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

                PhoneOptInRequest? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<PhoneOptInRequest>(body, new JsonSerializerOptions
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

                if (payload.OptInRequest != true)
                {
                    return await BadRequest(response, "optInRequest must be true.");
                }

                var trimmedPhone = NormalizePhoneNumber(payload.PhoneNumber);
                if (string.IsNullOrWhiteSpace(trimmedPhone))
                {
                    return await BadRequest(response, "phoneNumber is invalid.");
                }

                var trimmedLeadFrom = payload.LeadFrom?.Trim();
                if (string.IsNullOrWhiteSpace(trimmedLeadFrom))
                {
                    trimmedLeadFrom = null;
                }

                int phoneId;
                bool existingPhoneRecord = false;
                bool smsOptInConfirmed = false;
                string status = "Pending";

                const string selectExistingSql = @"
SELECT TOP 1 [PhoneId], [SmsOptInConfirmed], [Status]
FROM [dbo].[PhoneNumbers]
WHERE [PhoneNumber] = @PhoneNumber
ORDER BY [PhoneId] DESC;";

                const string insertSql = @"
INSERT INTO [dbo].[PhoneNumbers]
    ([PhoneNumber], [SmsOptInRequested], [SmsOptInConfirmed], [OptInRequestedDate], [OptInConfirmedDate], [Status], [LeadFrom])
VALUES
    (@PhoneNumber, @SmsOptInRequested, @SmsOptInConfirmed, @OptInRequestedDate, @OptInConfirmedDate, @Status, @LeadFrom);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                const string updateExistingSql = @"
UPDATE [dbo].[PhoneNumbers]
SET
    [SmsOptInRequested] = @SmsOptInRequested,
    [OptInRequestedDate] = @OptInRequestedDate,
    [Status] = @Status,
    [LeadFrom] = COALESCE(@LeadFrom, [LeadFrom])
WHERE [PhoneId] = @PhoneId;";

                const string insertMessageSql = @"
INSERT INTO [dbo].[Messages]
    ([PhoneId], [Direction], [MessageText], [Timestamp], [DeliveryStatus])
VALUES
    (@PhoneId, @Direction, @MessageText, @Timestamp, @DeliveryStatus);";

                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var findCommand = new SqlCommand(selectExistingSql, connection))
                    {
                        findCommand.Parameters.AddWithValue("@PhoneNumber", trimmedPhone);

                        using var reader = await findCommand.ExecuteReaderAsync();
                        if (await reader.ReadAsync())
                        {
                            existingPhoneRecord = true;
                            phoneId = reader.GetInt32(reader.GetOrdinal("PhoneId"));
                            smsOptInConfirmed = reader["SmsOptInConfirmed"] != DBNull.Value && Convert.ToBoolean(reader["SmsOptInConfirmed"]);
                            status = reader["Status"] == DBNull.Value ? "Pending" : Convert.ToString(reader["Status"]) ?? "Pending";
                        }
                        else
                        {
                            phoneId = 0;
                        }
                    }

                    if (!existingPhoneRecord)
                    {
                        using var insertCommand = new SqlCommand(insertSql, connection);
                        insertCommand.Parameters.AddWithValue("@PhoneNumber", trimmedPhone);
                        insertCommand.Parameters.AddWithValue("@SmsOptInRequested", true);
                        insertCommand.Parameters.AddWithValue("@SmsOptInConfirmed", false);
                        insertCommand.Parameters.AddWithValue("@OptInRequestedDate", DateTime.UtcNow);
                        insertCommand.Parameters.AddWithValue("@OptInConfirmedDate", DBNull.Value);
                        insertCommand.Parameters.AddWithValue("@Status", "Pending");
                        insertCommand.Parameters.AddWithValue("@LeadFrom", (object?)trimmedLeadFrom ?? DBNull.Value);

                        var result = await insertCommand.ExecuteScalarAsync();
                        if (result == null || result == DBNull.Value)
                        {
                            _logger.LogError("Insert completed but PhoneId was not returned.");
                            return await ServerError(response, "Failed to create opt-in record.");
                        }

                        phoneId = Convert.ToInt32(result);
                        smsOptInConfirmed = false;
                        status = "Pending";
                    }
                    else if (!smsOptInConfirmed || !string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase))
                    {
                        status = "Pending";
                        using var updateCommand = new SqlCommand(updateExistingSql, connection);
                        updateCommand.Parameters.AddWithValue("@SmsOptInRequested", true);
                        updateCommand.Parameters.AddWithValue("@OptInRequestedDate", DateTime.UtcNow);
                        updateCommand.Parameters.AddWithValue("@Status", status);
                        updateCommand.Parameters.AddWithValue("@LeadFrom", (object?)trimmedLeadFrom ?? DBNull.Value);
                        updateCommand.Parameters.AddWithValue("@PhoneId", phoneId);
                        await updateCommand.ExecuteNonQueryAsync();
                    }

                    if (!smsOptInConfirmed)
                    {
                        const string optInPrompt = "Please opt in for messages from Forest Lake Auto Truck & Trailer. Reply 'YES' to confirm; data rates may apply. Reply STOP to unsubscribe.";
                        using var messageCommand = new SqlCommand(insertMessageSql, connection);
                        messageCommand.Parameters.AddWithValue("@PhoneId", phoneId);
                        messageCommand.Parameters.AddWithValue("@Direction", "Outbound");
                        messageCommand.Parameters.AddWithValue("@MessageText", optInPrompt);
                        messageCommand.Parameters.AddWithValue("@Timestamp", DateTime.UtcNow);
                        messageCommand.Parameters.AddWithValue("@DeliveryStatus", "Queued");
                        await messageCommand.ExecuteNonQueryAsync();
                    }
                }

                response.StatusCode = existingPhoneRecord ? HttpStatusCode.OK : HttpStatusCode.Created;
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await response.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    success = true,
                    phoneId,
                    existingPhoneRecord,
                    phoneNumber = trimmedPhone,
                    smsOptInRequested = true,
                    smsOptInConfirmed,
                    status,
                    leadFrom = trimmedLeadFrom,
                    message = smsOptInConfirmed
                        ? "Phone number is already opted in and active."
                        : "Phone opt-in request recorded. Confirmation prompt queued."
                }));

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in CreatePhoneOptIn");
                return await ServerError(response, "An internal server error occurred.");
            }
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

        private class PhoneOptInRequest
        {
            public string? PhoneNumber { get; set; }
            public bool? OptInRequest { get; set; }
            public string? LeadFrom { get; set; }
        }
    }
}