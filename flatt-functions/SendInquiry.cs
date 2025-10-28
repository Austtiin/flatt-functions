using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Azure;
using Azure.Communication.Email;
using Azure.Core;
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

        private record InquiryRequest(
            string? UserEmail,
            string? Subject,
            string? Message,
            string? Name,
            string? Phone,
            int? UnitId,
            string? Vin,
            Dictionary<string, object>? Meta
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
                // Read raw body for logging and robust error reporting
                string raw = (await new StreamReader(req.Body).ReadToEndAsync()) ?? string.Empty;

                var invocationId = executionContext?.InvocationId.ToString() ?? string.Empty;
                string contentType = req.Headers.TryGetValues("Content-Type", out var ctVals) ? string.Join(",", ctVals) : "";
                string contentLength = req.Headers.TryGetValues("Content-Length", out var clVals) ? string.Join(",", clVals) : "";
                string userAgent = req.Headers.TryGetValues("User-Agent", out var uaVals) ? string.Join(",", uaVals) : "";
                string xff = req.Headers.TryGetValues("x-forwarded-for", out var xffVals) ? string.Join(",", xffVals) : "";

                _logger.LogInformation(
                    "Inquiry received invId={invId} ct={ct} cl={cl} ua={ua} xff={xff} rawChars={rawLen}",
                    invocationId, contentType, contentLength, userAgent, xff, raw?.Length ?? 0);

                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var body = JsonSerializer.Deserialize<InquiryRequest>(root.GetRawText(), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                // Fallbacks for common client-side mismatches:
                // - Accept "body"/"Body" as an alias for "message"
                if (body is not null && string.IsNullOrWhiteSpace(body.Message))
                {
                    if (TryGetCaseInsensitiveString(root, "body", out var altMsg) && !string.IsNullOrWhiteSpace(altMsg))
                    {
                        body = body with { Message = altMsg };
                    }
                }

                if (body is null || string.IsNullOrWhiteSpace(body.UserEmail) || string.IsNullOrWhiteSpace(body.Message))
                {
                    _logger.LogWarning("Inquiry validation failed invId={invId}: missing userEmail or message. Raw: {raw}", invocationId, Truncate(raw, 2000));
                    return await BadRequest(res, "userEmail and message are required.");
                }

                // Read sender, recipient, and connection string ONLY from environment variables
                var fromAddress = Environment.GetEnvironmentVariable("EmailFrom");
                if (string.IsNullOrWhiteSpace(fromAddress))
                {
                    return await Error(res, "EmailFrom environment variable is not set. Example: DoNotReply@<resource-guid>.azurecomm.net");
                }
                _logger.LogInformation("Inquiry using sender address: {from}", fromAddress);

                var salesAddress = Environment.GetEnvironmentVariable("SendToEmail");
                if (string.IsNullOrWhiteSpace(salesAddress))
                {
                    return await Error(res, "SendToEmail environment variable is not set. Example: sales@yourdomain.com");
                }
                _logger.LogInformation("Inquiry using sales/notification address: {sales}", salesAddress);

                var emailConn = Environment.GetEnvironmentVariable("EmailConnectionString");
                if (string.IsNullOrWhiteSpace(emailConn))
                {
                    return await Error(res, "EmailConnectionString environment variable is not set.");
                }

                var client = new EmailClient(emailConn);

                // Build content: dealership branding + details
                string subject = string.IsNullOrWhiteSpace(body.Subject) ? "We received your inquiry" : body.Subject!.Trim();
                string userName = string.IsNullOrWhiteSpace(body.Name) ? body.UserEmail! : body.Name!.Trim();

                // Defaults with optional overrides from configuration (these can remain config-based)
                string dealerName = _config["Dealer:Name"] ?? "IceCastleUSA.com";
                string dealerSite = _config["Dealer:SiteUrl"] ?? "https://IceCastleUSA.com";
                string dealerPhone = _config["Dealer:Phone"] ?? "(651) 272-5474";
                string dealerPhoneHref = _config["Dealer:PhoneHref"] ?? "+16512725474";
                string dealerEmail = _config["Dealer:Email"] ?? "sales@icecastleusa.com";
                string dealerAddress1 = _config["Dealer:Address1"] ?? "356 19th St SW";
                string dealerAddress2 = _config["Dealer:Address2"] ?? "Forest Lake, MN 55025";
                string dealerCountry = _config["Dealer:Country"] ?? "United States";
                string dealerHours = _config["Dealer:Hours"] ?? "Monday - Friday: 9:00 AM - 6:00 PM\nSaturday: 9:00 AM - 4:00 PM\nSunday: Closed";
                string dealerMapUrl = _config["Dealer:MapUrl"] ?? "https://maps.google.com/?q=356+19th+St+SW+Forest+Lake+MN+55025";
                string logoUrl = _config["EmailLogoUrl"] ?? string.Empty; // optional external logo URL

                string Safe(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
                string nl2br(string? s) => Safe(s).Replace("\n", "<br/>");

                // Meta details table
                string metaHtml = string.Empty;
                if (body.Meta != null && body.Meta.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (var kv in body.Meta)
                        parts.Add($"<tr><td style='padding:6px 8px;color:#666;border-bottom:1px solid #eee'>{Safe(kv.Key)}</td><td style='padding:6px 8px;border-bottom:1px solid #eee'>{Safe(kv.Value?.ToString())}</td></tr>");
                    metaHtml = $"<table width='100%' style='border-collapse:collapse;margin-top:8px'>{string.Join(string.Empty, parts)}</table>";
                }

                string submittedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

                string commonDetails = $@"<table width='100%' style='border-collapse:collapse;margin-top:8px'>
                    <tr><td style='padding:6px 8px;color:#666;width:140px'>Name</td><td style='padding:6px 8px'>{Safe(body.Name)}</td></tr>
                    <tr><td style='padding:6px 8px;color:#666'>Email</td><td style='padding:6px 8px'><a href='mailto:{Safe(body.UserEmail)}'>{Safe(body.UserEmail)}</a></td></tr>
                    <tr><td style='padding:6px 8px;color:#666'>Phone</td><td style='padding:6px 8px'><a href='tel:{Safe(body.Phone)}'>{Safe(body.Phone)}</a></td></tr>
                    <tr><td style='padding:6px 8px;color:#666'>Unit ID</td><td style='padding:6px 8px'>{Safe(body.UnitId?.ToString())}</td></tr>
                    <tr><td style='padding:6px 8px;color:#666'>VIN</td><td style='padding:6px 8px'>{Safe(body.Vin)}</td></tr>
                    <tr><td style='padding:6px 8px;color:#666'>Submitted</td><td style='padding:6px 8px'>{submittedUtc}</td></tr>
                </table>";

                string headerLogo = string.IsNullOrWhiteSpace(logoUrl)
                    ? $"<div style='font-size:22px;font-weight:800;letter-spacing:0.5px'>{Safe(dealerName)}</div>"
                    : $"<img src='{Safe(logoUrl)}' alt='{Safe(dealerName)}' style='max-width:220px;display:block;margin:0 auto'/>";

                string footer = $@"<table width='100%' style='background:#111;color:#fff;padding:16px;border-radius:8px'>
                    <tr>
                        <td style='vertical-align:top'>
                            <div style='font-weight:700;font-size:14px;color:#fff'>Visit Our Lot</div>
                            <div style='color:#ddd'>{Safe(dealerAddress1)}<br/>{Safe(dealerAddress2)}<br/>{Safe(dealerCountry)}</div>
                            <div style='margin-top:6px'><a href='{Safe(dealerMapUrl)}' style='color:#ff4040;text-decoration:none'>Get Directions →</a></div>
                        </td>
                        <td style='vertical-align:top'>
                            <div style='font-weight:700;font-size:14px;color:#fff'>Call Us</div>
                            <div><a href='tel:{Safe(dealerPhoneHref)}' style='color:#fff;text-decoration:none'>{Safe(dealerPhone)}</a></div>
                            <div style='font-weight:700;font-size:14px;color:#fff;margin-top:10px'>Email Us</div>
                            <div><a href='mailto:{Safe(dealerEmail)}' style='color:#fff;text-decoration:none'>{Safe(dealerEmail)}</a></div>
                        </td>
                        <td style='vertical-align:top'>
                            <div style='font-weight:700;font-size:14px;color:#fff'>Business Hours</div>
                            <div style='color:#ddd'>{nl2br(dealerHours)}</div>
                        </td>
                    </tr>
                </table>";

                string baseCardStart = $@"<div style='max-width:640px;margin:0 auto;padding:16px;font-family:Segoe UI,Roboto,Helvetica,Arial,sans-serif;background:#f8f8f8'>
                    <div style='background:#c40000;color:#fff;padding:14px 18px;border-radius:10px 10px 0 0;text-align:center'>
                        {headerLogo}
                    </div>
                    <div style='background:#fff;border:1px solid #eee;border-top:0;padding:18px;border-radius:0 0 10px 10px'>";
                string baseCardEnd = "</div>" + footer + "</div>";

                var confirmHtml = $@"<html><body>
                    {baseCardStart}
                        <h2 style='margin:0 0 8px 0;color:#111'>Thanks for your inquiry</h2>
                        <p style='margin:6px 0;color:#333'>Hi {Safe(userName)}, we received your message and a team member will contact you soon.</p>
                        {commonDetails}
                        <div style='margin-top:12px'>
                            <div style='font-weight:700;color:#111;margin-bottom:6px'>Your message</div>
                            <div style='background:#fafafa;border:1px solid #eee;border-radius:6px;padding:10px'>{Safe(body.Message)}</div>
                        </div>
                        {metaHtml}
                        <div style='margin-top:16px'>
                            <a href='{Safe(dealerSite)}' style='display:inline-block;background:#c40000;color:#fff;text-decoration:none;padding:10px 14px;border-radius:6px'>Visit IceCastleUSA.com</a>
                        </div>
                    {baseCardEnd}
                </body></html>";

                var salesHtml = $@"<html><body>
                    {baseCardStart}
                        <h2 style='margin:0 0 8px 0;color:#111'>New submission</h2>
                        {commonDetails}
                        <div style='margin-top:12px'>
                            <div style='font-weight:700;color:#111;margin-bottom:6px'>Message</div>
                            <div style='background:#fafafa;border:1px solid #eee;border-radius:6px;padding:10px'>{Safe(body.Message)}</div>
                        </div>
                        {metaHtml}
                    {baseCardEnd}
                </body></html>";

                // Send confirmation to user
                var confirmContent = new EmailContent($"Thanks for your inquiry • {dealerName}")
                {
                    Html = confirmHtml,
                    PlainText = $"Hi {userName},\n\nThanks for your inquiry. We received your message and will get back to you soon.\n\n--\nFuhrent"
                };
                var confirmMsg = new EmailMessage(
                    senderAddress: fromAddress,
                    content: confirmContent,
                    recipients: new EmailRecipients(new List<EmailAddress> { new EmailAddress(body.UserEmail!) })
                );
                confirmMsg.ReplyTo.Add(new EmailAddress(salesAddress));

                // Send notification to sales
                var salesContent = new EmailContent($"New submission • {dealerName}")
                {
                    Html = salesHtml,
                    PlainText = $"New submission from {userName} <{body.UserEmail}> at {submittedUtc}.\n\nMessage:\n{body.Message}"
                };
                var salesMsg = new EmailMessage(
                    senderAddress: fromAddress,
                    content: salesContent,
                    recipients: new EmailRecipients(new List<EmailAddress> { new EmailAddress(salesAddress) })
                );
                salesMsg.ReplyTo.Add(new EmailAddress(body.UserEmail!));

                _logger.LogInformation(
                    "Inquiry email send starting invId={invId} from={from} toUser={toUser} toSales={toSales} subjUserLen={s1} subjSalesLen={s2}",
                    invocationId, fromAddress, body.UserEmail, salesAddress,
                    confirmContent.Subject?.Length ?? 0, salesContent.Subject?.Length ?? 0);

                // Fire both sends (no need to block until completed; start and return ids)
                EmailSendOperation confirmOp = await client.SendAsync(WaitUntil.Started, confirmMsg);
                EmailSendOperation salesOp = await client.SendAsync(WaitUntil.Started, salesMsg);

                _logger.LogInformation(
                    "Inquiry email send accepted invId={invId} confirmOpId={cid} salesOpId={sid}",
                    invocationId, confirmOp.Id, salesOp.Id);

                res.StatusCode = HttpStatusCode.Accepted;
                res.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await res.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    ok = true,
                    confirmOperationId = confirmOp.Id,
                    salesOperationId = salesOp.Id
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

                _logger.LogInformation("Inquiry response sent invId={invId} status=202", invocationId);
                return res;
            }
            catch (JsonException)
            {
                // Invalid JSON payload
                _logger.LogWarning("Inquiry invalid JSON invId={invId}", executionContext?.InvocationId);
                return await BadRequest(res, "Invalid JSON. Send application/json with at least userEmail and message fields.");
            }
            catch (RequestFailedException rfe)
            {
                _logger.LogError(rfe, "Inquiry email send failed invId={invId}: {code} {msg}", executionContext?.InvocationId, rfe.ErrorCode, rfe.Message);
                return await Error(res, "Failed to send email. Please try again later.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SendInquiry failed invId={invId}", executionContext?.InvocationId);
                return await Error(res, ex.Message);
            }
        }

        private static string Truncate(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private static bool TryGetCaseInsensitiveString(JsonElement obj, string name, out string? value)
        {
            value = null;
            if (obj.ValueKind != JsonValueKind.Object) return false;
            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        value = prop.Value.GetString();
                        return true;
                    }
                    // If not a string, fallback to JSON text representation
                    value = prop.Value.ToString();
                    return true;
                }
            }
            return false;
        }

        private static void AddCors(HttpResponseData res)
        {
            res.Headers.Add("Access-Control-Allow-Origin", "*");
            res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            // Explicitly disable caching for inquiry responses
            res.Headers.Add("Cache-Control", "no-store, no-cache, must-revalidate, max-age=0");
            res.Headers.Add("Pragma", "no-cache");
            // Expires must be a valid HTTP-date; some frameworks reject "0"
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
