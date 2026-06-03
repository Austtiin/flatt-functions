using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Azure;
using Azure.Communication.Email;
using Azure.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace flatt_functions
{
    public class ProcessInquiryQueue
    {
        private readonly ILogger<ProcessInquiryQueue> _logger;

        public ProcessInquiryQueue(ILogger<ProcessInquiryQueue> logger)
        {
            _logger = logger;
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

        private record QueueEnvelope(
            string CorrelationId,
            DateTime ReceivedAtUtc,
            InquiryRequest Inquiry
        );

        [Function("ProcessInquiryQueue")]
        public async Task RunAsync(
            [QueueTrigger("%InquiryQueueName%", Connection = "StorageConnection")] string queueMessage,
            FunctionContext context)
        {
            var dequeueCount = context.BindingContext.BindingData.TryGetValue("DequeueCount", out var dc) ? dc?.ToString() : "n/a";
            
            // Check if email service should be disabled (for DEV environment)
            // In PROD, this environment variable should not be set, so service runs normally
            var disableEmailService = Environment.GetEnvironmentVariable("DisableEmailService");
            var isEmailServiceDisabled = !string.IsNullOrWhiteSpace(disableEmailService) && 
                (disableEmailService.Equals("true", StringComparison.OrdinalIgnoreCase) || 
                 disableEmailService.Equals("1", StringComparison.OrdinalIgnoreCase));

            if (isEmailServiceDisabled)
            {
                _logger.LogInformation("Email service disabled via DisableEmailService environment variable - skipping queue message processing. DequeueCount={dc}, MessageLength={len}", dequeueCount, queueMessage?.Length ?? 0);
                return; // Exit immediately without processing the message
            }

            try
            {
                var envelope = JsonSerializer.Deserialize<QueueEnvelope>(queueMessage, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (envelope == null || envelope.Inquiry == null || string.IsNullOrWhiteSpace(envelope.Inquiry.UserEmail) || string.IsNullOrWhiteSpace(envelope.Inquiry.Message))
                {
                    _logger.LogWarning("Invalid inquiry payload; dropping. Raw: {raw}", queueMessage);
                    return; // Let it complete to avoid poison loop for malformed messages
                }

                var inquiry = envelope.Inquiry;

                var fromAddress = Environment.GetEnvironmentVariable("EmailFrom");
                var salesAddress = Environment.GetEnvironmentVariable("SalesEmail");
                var emailConn = Environment.GetEnvironmentVariable("EmailConnectionString");

                if (string.IsNullOrWhiteSpace(fromAddress) || string.IsNullOrWhiteSpace(salesAddress) || string.IsNullOrWhiteSpace(emailConn))
                {
                    throw new InvalidOperationException("EmailFrom, SalesEmail, and EmailConnectionString environment variables must be set.");
                }

                var client = new EmailClient(emailConn);

                // Dealership Branding (image-free header)
                var siteUrl1 = "https://IceCastleUSA.com";
                var siteUrl2 = "https://ForestLakeAuto.com";
                var phone = "(651) 272-5474";
                var address = "356 19th St SW, Forest Lake, MN 55025";
                var blue = "#0033a0";
                var red = "#c40000";
                var tagline = "Locally Owned and Operated";
                var year = DateTime.UtcNow.Year;

                string name = WebUtility.HtmlEncode(inquiry.Name ?? "Customer");
                string vin = WebUtility.HtmlEncode(inquiry.Vin ?? "N/A");
                string phoneText = WebUtility.HtmlEncode(inquiry.Phone ?? "Not provided");
                string message = WebUtility.HtmlEncode(inquiry.Message);

                // Trade-in details HTML
                string tradeInHtml = "";
                if (inquiry.TradeIn != null)
                {
                    var ti = inquiry.TradeIn;
                    bool hasTradeInInfo = !string.IsNullOrWhiteSpace(ti.Year) || !string.IsNullOrWhiteSpace(ti.Make) || !string.IsNullOrWhiteSpace(ti.Model) || !string.IsNullOrWhiteSpace(ti.MileageOrHours) || !string.IsNullOrWhiteSpace(ti.Condition);
                    if (hasTradeInInfo)
                    {
                        
                        tradeInHtml += "<div style='margin-top:20px;padding-top:16px;border-top:1px solid #e6ebf2;'><div style='font-size:12px;font-weight:700;letter-spacing:0.6px;text-transform:uppercase;color:#c40000;margin-bottom:8px;'>Trade-In Information</div><table style='width:100%;border-collapse:collapse;'>";
                        if (!string.IsNullOrWhiteSpace(ti.Year)) tradeInHtml += $"<tr><td style='padding:6px 8px;color:#0033a0;font-weight:600;'>Year</td><td style='padding:6px 8px;'>{WebUtility.HtmlEncode(ti.Year)}</td></tr>";
                        if (!string.IsNullOrWhiteSpace(ti.Make)) tradeInHtml += $"<tr><td style='padding:6px 8px;color:#0033a0;font-weight:600;'>Make</td><td style='padding:6px 8px;'>{WebUtility.HtmlEncode(ti.Make)}</td></tr>";
                        if (!string.IsNullOrWhiteSpace(ti.Model)) tradeInHtml += $"<tr><td style='padding:6px 8px;color:#0033a0;font-weight:600;'>Model</td><td style='padding:6px 8px;'>{WebUtility.HtmlEncode(ti.Model)}</td></tr>";
                        if (!string.IsNullOrWhiteSpace(ti.MileageOrHours)) tradeInHtml += $"<tr><td style='padding:6px 8px;color:#0033a0;font-weight:600;'>Mileage/Hours</td><td style='padding:6px 8px;'>{WebUtility.HtmlEncode(ti.MileageOrHours)}</td></tr>";
                        if (!string.IsNullOrWhiteSpace(ti.Condition)) tradeInHtml += $"<tr><td style='padding:6px 8px;color:#0033a0;font-weight:600;'>Condition</td><td style='padding:6px 8px;'>{WebUtility.HtmlEncode(ti.Condition)}</td></tr>";
                        tradeInHtml += "</table></div>";
                    }
                }

                // Item Information HTML (from UnitId/VIN and optional Meta/Item)
                string itemInfoHtml = "";
                {
                    var rows = new List<string>();
                    if (inquiry.UnitId.HasValue)
                    {
                        rows.Add($"<tr><td style='padding:6px 8px;color:#0033a0;font-weight:600;'>Unit ID</td><td style='padding:6px 8px;'>{WebUtility.HtmlEncode(inquiry.UnitId.Value.ToString())}</td></tr>");
                    }
                    if (!string.IsNullOrWhiteSpace(inquiry.Vin))
                    {
                        rows.Add($"<tr><td style='padding:6px 8px;color:#0033a0;font-weight:600;'>VIN</td><td style='padding:6px 8px;'>{WebUtility.HtmlEncode(inquiry.Vin)}</td></tr>");
                    }

                    // Render Meta.Item if provided as an object, else render simple Meta pairs
                    if (inquiry.Meta != null)
                    {
                        foreach (var kvp in inquiry.Meta)
                        {
                            var key = kvp.Key;
                            var val = kvp.Value;
                            if (string.Equals(key, "Item", StringComparison.OrdinalIgnoreCase) && val is JsonElement je && je.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var prop in je.EnumerateObject())
                                {
                                    var pVal = prop.Value.ValueKind switch
                                    {
                                        JsonValueKind.String => prop.Value.GetString(),
                                        JsonValueKind.Number => prop.Value.ToString(),
                                        JsonValueKind.True => "true",
                                        JsonValueKind.False => "false",
                                        _ => prop.Value.ToString()
                                    };
                                    rows.Add($"<tr><td style='padding:6px 8px;color:#0033a0;font-weight:600;'>{WebUtility.HtmlEncode(prop.Name)}</td><td style='padding:6px 8px;'>{WebUtility.HtmlEncode(pVal)}</td></tr>");
                                }
                            }
                            else if (val is JsonElement ve)
                            {
                                if (ve.ValueKind == JsonValueKind.String || ve.ValueKind == JsonValueKind.Number || ve.ValueKind == JsonValueKind.True || ve.ValueKind == JsonValueKind.False)
                                {
                                    rows.Add($"<tr><td style='padding:6px 8px;color:#0033a0;font-weight:600;'>{WebUtility.HtmlEncode(key)}</td><td style='padding:6px 8px;'>{WebUtility.HtmlEncode(ve.ToString())}</td></tr>");
                                }
                            }
                            else if (val != null)
                            {
                                rows.Add($"<tr><td style='padding:6px 8px;color:#0033a0;font-weight:600;'>{WebUtility.HtmlEncode(key)}</td><td style='padding:6px 8px;'>{WebUtility.HtmlEncode(val.ToString() ?? string.Empty)}</td></tr>");
                            }
                        }
                    }

                    if (rows.Count > 0)
                    {
                        itemInfoHtml = "<div style='margin-top:20px;padding-top:16px;border-top:1px solid #e6ebf2;'><div style='font-size:12px;font-weight:700;letter-spacing:0.6px;text-transform:uppercase;color:#c40000;margin-bottom:8px;'>Information</div><table style='width:100%;border-collapse:collapse;'>" + string.Join(string.Empty, rows) + "</table></div>";
                    }
                }

                // ----------- USER EMAIL -----------
               var userHtml = $@"
                <html lang='en'>
                <head>
                <meta name='viewport' content='width=device-width, initial-scale=1.0'>
                <meta charset='UTF-8'>
                <title>We’ve received your inquiry — Forest Lake Auto</title>
                </head>
                <body style='margin:0;padding:0;background-color:#eef1f5;font-family:Segoe UI,Helvetica,Arial,sans-serif;color:#2b2f36;-webkit-font-smoothing:antialiased;'>
                <table role='presentation' cellpadding='0' cellspacing='0' border='0' width='100%' style='background-color:#eef1f5;margin:0;padding:32px 12px;'>
                    <tr>
                    <td align='center'>
                        <table role='presentation' cellpadding='0' cellspacing='0' border='0' width='100%' style='max-width:600px;background:#ffffff;border-radius:16px;overflow:hidden;box-shadow:0 8px 24px rgba(15,30,60,0.10);'>

                        <!-- Header -->
                        <tr>
                            <td style='background:linear-gradient(135deg,{red} 0%,{blue} 100%);padding:28px 24px;text-align:center;'>
                            <div style='color:#ffffff;font-size:24px;font-weight:800;letter-spacing:0.5px;'>Forest Lake Auto</div>
                            <div style='color:#ffffff;opacity:0.85;font-size:13px;margin-top:4px;'>Truck &amp; Trailer Sales · {tagline}</div>
                            <div style='margin-top:16px;'>
                                <a href='{siteUrl1}' style='display:inline-block;color:#ffffff;text-decoration:none;font-size:13px;font-weight:600;margin:4px 6px;padding:7px 14px;background:rgba(255,255,255,0.15);border-radius:20px;'>IceCastleUSA.com</a>
                                <a href='{siteUrl2}' style='display:inline-block;color:#ffffff;text-decoration:none;font-size:13px;font-weight:600;margin:4px 6px;padding:7px 14px;background:rgba(255,255,255,0.15);border-radius:20px;'>ForestLakeAuto.com</a>
                                <a href='tel:+16512725474' style='display:inline-block;color:#ffffff;text-decoration:none;font-size:13px;font-weight:600;margin:4px 6px;padding:7px 14px;background:rgba(255,255,255,0.15);border-radius:20px;'>Call {phone}</a>
                            </div>
                            </td>
                        </tr>

                        <!-- Body -->
                        <tr>
                            <td style='padding:32px 28px 8px 28px;line-height:1.65;font-size:15px;'>
                            <h1 style='margin:0 0 12px 0;color:{blue};font-size:24px;font-weight:800;'>We’ve received your inquiry</h1>
                            <p style='margin:0 0 12px 0;'>Hi {name},</p>
                            <p style='margin:0 0 20px 0;color:#4a4f57;'>Thanks for reaching out to Forest Lake Auto Truck &amp; Trailer Sales! We appreciate your interest — a member of our team will be in touch with you soon.</p>

                            <div style='margin:0 0 8px 0;background:#f6f8fb;padding:20px;border-radius:12px;border:1px solid #e6ebf2;'>
                                <div style='font-size:12px;font-weight:700;letter-spacing:0.6px;text-transform:uppercase;color:{red};margin-bottom:6px;'>Your Message</div>
                                <p style='margin:0 0 16px 0;color:#2b2f36;'>{message}</p>
                                <table role='presentation' cellpadding='0' cellspacing='0' border='0' width='100%' style='border-collapse:collapse;'>
                                    <tr><td style='padding:6px 0;color:{blue};font-weight:600;width:140px;'>Phone</td><td style='padding:6px 0;color:#2b2f36;'>{phoneText}</td></tr>
                                    <tr><td style='padding:6px 0;color:{blue};font-weight:600;'>VIN / Unit ID</td><td style='padding:6px 0;color:#2b2f36;'>{vin}</td></tr>
                                </table>
                                {tradeInHtml}
                                {itemInfoHtml}
                            </div>

                            <table role='presentation' cellpadding='0' cellspacing='0' border='0' style='margin:24px auto 8px auto;'>
                                <tr><td style='border-radius:24px;background:{red};'>
                                    <a href='tel:+16512725474' style='display:inline-block;color:#ffffff;text-decoration:none;font-size:15px;font-weight:700;padding:13px 30px;'>Call us at {phone}</a>
                                </td></tr>
                            </table>

                            <p style='margin:16px 0 0 0;font-size:13px;color:#7a818b;text-align:center;'>
                                Details are provided as a reference and may change without notice.
                            </p>
                            </td>
                        </tr>

                        <!-- Footer Links -->
                        <tr>
                            <td style='padding:24px 20px 20px 20px;text-align:center;font-size:14px;color:#4a4f57;border-top:1px solid #eef1f5;'>
                            <strong style='color:#2b2f36;'>Forest Lake Auto Truck &amp; Trailer Sales</strong>
                            <div style='margin-top:8px;'>
                                <a href='tel:+16512725474' style='color:{blue};text-decoration:none;margin:0 8px;'>{phone}</a>
                                <span style='color:#c5ccd6;'>|</span>
                                <a href='{siteUrl1}' style='color:{red};text-decoration:none;margin:0 8px;'>IceCastleUSA.com</a>
                                <span style='color:#c5ccd6;'>|</span>
                                <a href='{siteUrl2}' style='color:{blue};text-decoration:none;margin:0 8px;'>ForestLakeAuto.com</a>
                            </div>
                            </td>
                        </tr>

                        <!-- Legal Footer -->
                        <tr>
                            <td style='background:#1f242c;color:#cfd5dd;padding:24px 20px;text-align:center;font-size:13px;line-height:1.6;'>
                            <p style='margin:0 0 6px 0;color:#ffffff;'><strong>Forest Lake Auto Truck &amp; Trailer Sales</strong></p>
                            <p style='margin:0 0 4px 0;'>{address}</p>
                            <p style='margin:0 0 12px 0;'>{phone}</p>
                            <p style='margin:0 0 8px 0;color:#8b929c;font-size:12px;'>
                                You received this email because you contacted Forest Lake Auto Truck &amp; Trailer Sales.<br>
                                This message is not a sales contract. Your information will not be shared or sold.
                            </p>
                            <p style='margin:0 0 6px 0;color:#8b929c;font-size:12px;'>
                                For more details, visit our
                                <a href='https://forestlakeauto.com/privacy' style='color:#ffffff;text-decoration:underline;'>Privacy Policy</a>.
                            </p>
                            <p style='margin:0;color:#6b727c;font-size:12px;'>© {year} Forest Lake Auto. All rights reserved.</p>
                            </td>
                        </tr>

                        </table>
                    </td>
                    </tr>
                </table>
                </body>
                </html>";

                var userMsg = new EmailMessage(
                    senderAddress: fromAddress,
                    content: new EmailContent("We’ve received your inquiry — Forest Lake Auto")
                    {
                        PlainText = $"Hi {name},\n\nThanks for reaching out to Forest Lake Auto Truck & Trailer Sales. We’ve received your message and a team member will contact you shortly.\n\nMessage:\n{inquiry.Message}\nPhone: {inquiry.Phone}\nVIN / Unit ID: {inquiry.Vin}\n\nInformation is subject to change without notice, DO NOT REPLY TO THIS EMAIL.\n\nForest Lake Auto Truck & Trailer Sales\n{phone}\n{address}",
                        Html = userHtml
                    },
                    recipients: new EmailRecipients(new List<EmailAddress> { new EmailAddress(inquiry.UserEmail!) })
                );

                // ----------- SALES EMAIL -----------
                var salesHtml = $@"
                <html>
                <body style='font-family:Segoe UI,Helvetica,Arial,sans-serif;background:#eef1f5;padding:0;margin:0;color:#2b2f36;-webkit-font-smoothing:antialiased;'>
                    <div style='max-width:600px;margin:32px auto;background:#ffffff;border-radius:16px;overflow:hidden;box-shadow:0 8px 24px rgba(15,30,60,0.10);'>
                        <!-- Header -->
                        <div style='background:linear-gradient(135deg,{red} 0%,{blue} 100%);padding:24px 24px;text-align:center;'>
                            <div style='color:#ffffff;font-size:13px;font-weight:700;letter-spacing:1px;text-transform:uppercase;opacity:0.85;'>New Lead</div>
                            <div style='color:#ffffff;font-size:22px;font-weight:800;margin-top:4px;'>Forest Lake Auto</div>
                        </div>

                        <div style='padding:28px 28px 18px 28px;line-height:1.6;'>
                            <h1 style='margin:0 0 16px 0;color:{blue};font-size:21px;font-weight:800;'>New customer inquiry</h1>
                            <table role='presentation' cellpadding='0' cellspacing='0' border='0' width='100%' style='border-collapse:collapse;background:#f6f8fb;border:1px solid #e6ebf2;border-radius:12px;'>
                                <tr><td style='padding:10px 14px;color:{blue};font-weight:600;width:140px;'>From</td><td style='padding:10px 14px;'>{WebUtility.HtmlEncode(inquiry.UserEmail)}</td></tr>
                                <tr><td style='padding:10px 14px;color:{blue};font-weight:600;border-top:1px solid #e6ebf2;'>Name</td><td style='padding:10px 14px;border-top:1px solid #e6ebf2;'>{name}</td></tr>
                                <tr><td style='padding:10px 14px;color:{blue};font-weight:600;border-top:1px solid #e6ebf2;'>Phone</td><td style='padding:10px 14px;border-top:1px solid #e6ebf2;'>{phoneText}</td></tr>
                                <tr><td style='padding:10px 14px;color:{blue};font-weight:600;border-top:1px solid #e6ebf2;'>VIN / Unit ID</td><td style='padding:10px 14px;border-top:1px solid #e6ebf2;'>{vin}</td></tr>
                            </table>
                            {tradeInHtml}
                            {itemInfoHtml}
                            <div style='margin-top:18px;background:#fff8f8;padding:16px;border-radius:12px;border:1px solid #f3dada;'>
                                <div style='font-size:12px;font-weight:700;letter-spacing:0.6px;text-transform:uppercase;color:{red};margin-bottom:6px;'>Message</div>
                                <div style='color:#2b2f36;'>{message}</div>
                            </div>
                            <table role='presentation' cellpadding='0' cellspacing='0' border='0' style='margin:22px 0 4px 0;'>
                                <tr><td style='border-radius:24px;background:{blue};'>
                                    <a href='mailto:{WebUtility.HtmlEncode(inquiry.UserEmail)}' style='display:inline-block;color:#ffffff;text-decoration:none;font-size:15px;font-weight:700;padding:12px 28px;'>Reply to customer</a>
                                </td></tr>
                            </table>
                        </div>

                        <div style='background:#1f242c;color:#cfd5dd;padding:20px 18px;text-align:center;font-size:13px;line-height:1.5;'>
                            <p style='margin:0 0 4px 0;color:#ffffff;'><b>Forest Lake Auto Truck &amp; Trailer Sales</b></p>
                            <p style='margin:0 0 2px 0;'>{address}</p>
                            <p style='margin:0 0 8px 0;'>{phone}</p>
                            <p style='margin:0;color:#8b929c;font-size:12px;'>Generated from a website inquiry. This message is not a sales contract. © {year} Forest Lake Auto.</p>
                        </div>
                    </div>
                </body></html>";

                var salesMsg = new EmailMessage(
                    senderAddress: fromAddress,
                    content: new EmailContent($"New website inquiry — {name} ({inquiry.UserEmail})")
                    {
                        PlainText = $"New customer inquiry\n\nName: {inquiry.Name}\nEmail: {inquiry.UserEmail}\nPhone: {inquiry.Phone}\nVIN / Unit ID: {inquiry.Vin}\n\nMessage:\n{inquiry.Message}\n\nForest Lake Auto Truck & Trailer Sales",
                        Html = salesHtml
                    },
                    recipients: new EmailRecipients(new List<EmailAddress> { new EmailAddress(salesAddress) })
                );

                // Send both emails concurrently with transient handling; let exceptions bubble for retry
                var sendUserTask = client.SendAsync(WaitUntil.Completed, userMsg);
                var sendSalesTask = client.SendAsync(WaitUntil.Completed, salesMsg);
                try
                {
                    await Task.WhenAll(sendUserTask, sendSalesTask);
                }
                catch (RequestFailedException ex) when (ex.Status == 429 || ex.Status == 503)
                {
                    _logger.LogWarning("Transient failure, delaying retry: {msg}", ex.Message);
                    throw; // Let Azure Functions retry automatically
                }

                _logger.LogInformation(
                    "Inquiry emails sent: CorrelationId={cid}, User={user}, UnitId={unitId}, Vin={vin}, DequeueCount={dc}",
                    envelope.CorrelationId,
                    inquiry.UserEmail,
                    inquiry.UnitId,
                    inquiry.Vin,
                    dequeueCount);
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Azure Email Service failed. DequeueCount={dc}", dequeueCount);
                throw; // Let Functions retry and eventually DLQ
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception processing inquiry. DequeueCount={dc}", dequeueCount);
                throw; // trigger retry/DLQ
            }
        }
    }
}

