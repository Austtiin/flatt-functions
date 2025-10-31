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
                        tradeInHtml += "<div style='margin-top:24px;'><h3 style='color:#c40000;margin-bottom:8px;'>Trade-In Information</h3><table style='width:100%;border-collapse:collapse;'>";
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
                        itemInfoHtml = "<div style='margin-top:24px;'><h3 style='color:#c40000;margin-bottom:8px;'>Unit Information</h3><table style='width:100%;border-collapse:collapse;'>" + string.Join(string.Empty, rows) + "</table></div>";
                    }
                }

                // ----------- USER EMAIL -----------
                var userHtml = $@"
                <html>
                <body style='font-family:Segoe UI,Arial,sans-serif;background:#f8f8f8;padding:0;margin:0;'>
                    <div style='max-width:600px;margin:24px auto;background:#fff;border-radius:12px;overflow:hidden;border:1px solid #eee;'>
                        <!-- Brand Nav Header (no images) -->
                        <div style='background:linear-gradient(90deg,{red} 0%,{blue} 100%);padding:14px 16px;text-align:center;'>
                            <div style='font-size:0;'>
                                <a href='{siteUrl1}' style='display:inline-block;color:#fff;text-decoration:none;font-size:14px;font-weight:700;margin:0 10px;'>IceCastleUSA.com</a>
                                <span style='display:inline-block;color:#ffffff88;font-size:14px;margin:0 6px;'>|</span>
                                <a href='{siteUrl2}' style='display:inline-block;color:#fff;text-decoration:none;font-size:14px;font-weight:700;margin:0 10px;'>ForestLakeAuto.com</a>
                                <span style='display:inline-block;color:#ffffff88;font-size:14px;margin:0 6px;'>|</span>
                                <a href='tel:+16512725474' style='display:inline-block;color:#fff;text-decoration:none;font-size:14px;font-weight:700;margin:0 10px;'>Call {phone}</a>
                            </div>
                            <div style='color:#fff;opacity:0.85;font-size:12px;margin-top:6px;'>{tagline}</div>
                        </div>

                        <div style='padding:22px 20px 18px 20px;line-height:1.55;'>
                            <h2 style='margin:0 0 6px 0;color:{blue};font-size:20px;'>We’ve received your inquiry</h2>
                            <p style='margin:0 0 12px 0;color:#333;'>Hi {name},</p>
                            <p style='margin:0 0 12px 0;color:#333;'>Thanks for reaching out to Forest Lake Auto Truck & Trailer Sales. We’ve received your message and a team member will contact you shortly. Below is a confirmation of what you submitted.</p>

                            <div style='margin:14px 0 0 0;background:#fafafa;padding:12px;border-radius:8px;border:1px solid #eee;'>
                                <p style='margin:0 0 8px 0;'><b>Message</b><br>{message}</p>
                                <p style='margin:0 0 6px 0;'><b>Phone:</b> {phoneText}</p>
                                <p style='margin:0 0 6px 0;'><b>VIN / Unit ID:</b> {vin}</p>
                                <div style='height:1px;background:{blue};opacity:0.12;margin:12px 0;'></div>
                                {tradeInHtml}
                                {itemInfoHtml}
                            </div>

                            <p style='margin:14px 0 0 0;color:#444;font-size:14px;'>All information subject to change. For urgent questions, call us at <b>{phone}</b>.</p>
                        </div>

                        <div style='background:#f3f3f3;padding:12px 14px;text-align:center;font-size:14px;color:#333;'>
                            <b>Forest Lake Auto Truck & Trailer Sales</b>
                            <div style='margin-top:6px;'>
                                <a href='tel:+16512725474' style='color:{blue};text-decoration:none;margin:0 8px;'>{phone}</a>
                                <span style='color:#888;'>|</span>
                                <a href='{siteUrl1}' style='color:{red};text-decoration:none;margin:0 8px;'>IceCastleUSA.com</a>
                                <span style='color:#888;'>|</span>
                                <a href='{siteUrl2}' style='color:{blue};text-decoration:none;margin:0 8px;'>ForestLakeAuto.com</a>
                            </div>
                        </div>

                        <div style='background:#222;color:#fff;padding:16px 14px;text-align:center;font-size:13px;line-height:1.5;'>
                            <p style='margin:0 0 4px 0;'><b>Forest Lake Auto Truck & Trailer Sales</b></p>
                            <p style='margin:0 0 2px 0;'>{address}</p>
                            <p style='margin:0 0 6px 0;'>{phone}</p>
                            <p style='margin:0;color:#bbb;font-size:12px;'>You received this email because you contacted Forest Lake Auto Truck & Trailer Sales. This message is not a sales contract. © {year} Forest Lake Auto.</p>
                        </div>
                    </div>
                </body></html>";

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
                <body style='font-family:Segoe UI,Arial,sans-serif;background:#f8f8f8;padding:0;margin:0;'>
                    <div style='max-width:600px;margin:24px auto;background:#fff;border-radius:12px;overflow:hidden;border:1px solid #eee;'>
                        <!-- Brand Nav Header (no images) -->
                        <div style='background:linear-gradient(90deg,{red} 0%,{blue} 100%);padding:14px 16px;text-align:center;'>
                            <div style='font-size:0;'>
                                <a href='{siteUrl1}' style='display:inline-block;color:#fff;text-decoration:none;font-size:14px;font-weight:700;margin:0 10px;'>IceCastleUSA.com</a>
                                <span style='display:inline-block;color:#ffffff88;font-size:14px;margin:0 6px;'>|</span>
                                <a href='{siteUrl2}' style='display:inline-block;color:#fff;text-decoration:none;font-size:14px;font-weight:700;margin:0 10px;'>ForestLakeAuto.com</a>
                                <span style='display:inline-block;color:#ffffff88;font-size:14px;margin:0 6px;'>|</span>
                                <a href='tel:+16512725474' style='display:inline-block;color:#fff;text-decoration:none;font-size:14px;font-weight:700;margin:0 10px;'>Call {phone}</a>
                            </div>
                            <div style='color:#fff;opacity:0.85;font-size:12px;margin-top:6px;'>{tagline}</div>
                        </div>

                        <div style='padding:22px 20px 18px 20px;line-height:1.55;'>
                            <h2 style='margin:0 0 6px 0;color:{blue};font-size:20px;'>New customer inquiry</h2>
                            <p style='margin:0 0 6px 0;color:#333;'><b>From:</b> {WebUtility.HtmlEncode(inquiry.UserEmail)}</p>
                            <p style='margin:0 0 6px 0;color:#333;'><b>Name:</b> {name}</p>
                            <p style='margin:0 0 6px 0;color:#333;'><b>Phone:</b> {phoneText}</p>
                            <p style='margin:0 0 10px 0;color:#333;'><b>VIN / Unit ID:</b> {vin}</p>
                            <div style='height:1px;background:{blue};opacity:0.12;margin:12px 0;'></div>
                            {tradeInHtml}
                            {itemInfoHtml}
                            <div style='margin-top:12px;background:#fafafa;padding:12px;border-radius:8px;border:1px solid #eee;'>
                                <b>Message:</b><br>{message}
                            </div>
                        </div>
                        <div style='background:#f3f3f3;padding:12px 14px;text-align:center;font-size:14px;color:#333;'>
                            <b>Forest Lake Auto Truck & Trailer Sales</b>
                            <div style='margin-top:6px;'>
                                <a href='tel:+16512725474' style='color:{blue};text-decoration:none;margin:0 8px;'>{phone}</a>
                                <span style='color:#888;'>|</span>
                                <a href='{siteUrl1}' style='color:{red};text-decoration:none;margin:0 8px;'>IceCastleUSA.com</a>
                                <span style='color:#888;'>|</span>
                                <a href='{siteUrl2}' style='color:{blue};text-decoration:none;margin:0 8px;'>ForestLakeAuto.com</a>
                            </div>
                        </div>

                        <div style='background:#222;color:#fff;padding:16px 14px;text-align:center;font-size:13px;line-height:1.5;'>
                            <p style='margin:0 0 4px 0;'><b>Forest Lake Auto Truck & Trailer Sales</b></p>
                            <p style='margin:0 0 2px 0;'>{address}</p>
                            <p style='margin:0 0 6px 0;'>{phone}</p>
                            <p style='margin:0;color:#bbb;font-size:12px;'>You received this email because a customer contacted Forest Lake Auto Truck & Trailer Sales via the website. This message is not a sales contract. © {year} Forest Lake Auto.</p>
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
