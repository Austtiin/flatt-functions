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

                var fromAddress = Environment.GetEnvironmentVariable("EmailFrom");
                var salesAddress = Environment.GetEnvironmentVariable("SalesEmail");
                var emailConn = Environment.GetEnvironmentVariable("EmailConnectionString");

                if (string.IsNullOrWhiteSpace(fromAddress) || string.IsNullOrWhiteSpace(salesAddress) || string.IsNullOrWhiteSpace(emailConn))
                    return await Error(res, "EmailFrom, SalesEmail, and EmailConnectionString environment variables must be set.");

                var client = new EmailClient(emailConn);

                // Dealership Branding
                var logoUrl = "https://icecastleusa.com/assets/img/logo-icecastleusa.png";
                var siteUrl1 = "https://IceCastleUSA.com";
                var siteUrl2 = "https://ForestLakeAuto.com";
                var phone = "(651) 272-5474";
                var address = "356 19th St SW, Forest Lake, MN 55025";
                var mapUrl = "https://maps.google.com/?q=356+19th+St+SW+Forest+Lake+MN+55025";
                var blue = "#0033a0";
                var red = "#c40000";

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

                // ----------- USER EMAIL -----------
                var userHtml = $@"
                <html>
                <body style='font-family:Segoe UI,Arial,sans-serif;background:#f8f8f8;padding:0;margin:0;'>
                    <div style='max-width:600px;margin:32px auto;background:#fff;border-radius:12px;overflow:hidden;border:1px solid #eee;'>
                        <div style='background:linear-gradient(90deg,{red} 0%,{blue} 100%);padding:24px;text-align:center;'>
                            <img src='{logoUrl}' alt='Ice Castle USA' style='max-width:180px;margin-bottom:10px;'/>
                            <h2 style='color:#fff;margin:0;'>Thank You for Your Inquiry!</h2>
                        </div>
                        <div style='padding:28px 24px 18px 24px;'>
                            <p>Hi {name},</p>
                            <p>We’ve received your inquiry and one of our team members will get back to you soon. Below is a copy of your message for your records.</p>

                            <div style='margin-top:20px;background:#fafafa;padding:12px;border-radius:8px;border:1px solid #eee;'>
                                <p style='margin:0;'><b>Message:</b><br>{message}</p>
                                <p style='margin:8px 0 0;'><b>Phone:</b> {phoneText}</p>
                                <p style='margin:4px 0 0;'><b>VIN / Unit ID:</b> {vin}</p>
                                {tradeInHtml}
                            </div>

                            <div style='margin:32px 0;text-align:center;'>
                                <a href='{siteUrl1}' style='background:{blue};color:#fff;text-decoration:none;padding:12px 24px;border-radius:6px;font-weight:600;margin:0 8px;'>Visit IceCastleUSA.com</a>
                                <a href='{siteUrl2}' style='background:{red};color:#fff;text-decoration:none;padding:12px 24px;border-radius:6px;font-weight:600;margin:0 8px;'>ForestLakeAuto.com</a>
                            </div>

                            <p style='font-size:14px;color:#444;text-align:center;margin-top:20px;'>If you have any urgent questions, call us directly at <b>{phone}</b>.</p>
                        </div>

                        <div style='background:#222;color:#fff;padding:18px;text-align:center;font-size:14px;'>
                            <p style='margin:0;'><b>Forest Lake Auto Truck & Trailer Sales</b></p>
                            <p style='margin:0;'>{address}</p>
                            <p style='margin:0;'>{phone}</p>
                            <p style='margin:0;'>Mon–Fri: 9AM–4PM | Sat: 9AM–4PM</p>
                        </div>
                    </div>
                </body></html>";

                var userMsg = new EmailMessage(
                    senderAddress: fromAddress,
                    content: new EmailContent("Thank You for Your Inquiry – Forest Lake Auto / Ice Castle USA")
                    {
                        PlainText = $"Hi {name},\n\nWe received your inquiry and will contact you soon.\n\nMessage:\n{inquiry.Message}\nPhone: {inquiry.Phone}\nVIN: {inquiry.Vin}\n\nThank you,\nForest Lake Auto Truck & Trailer Sales\n{phone}\n{address}",
                        Html = userHtml
                    },
                    recipients: new EmailRecipients(new List<EmailAddress> { new EmailAddress(inquiry.UserEmail) })
                );

                // ----------- SALES EMAIL -----------
                var salesHtml = $@"
                <html>
                <body style='font-family:Segoe UI,Arial,sans-serif;background:#f8f8f8;padding:0;margin:0;'>
                    <div style='max-width:600px;margin:32px auto;background:#fff;border-radius:12px;overflow:hidden;border:1px solid #eee;'>
                        <div style='background:linear-gradient(90deg,{red} 0%,{blue} 100%);padding:24px;text-align:center;'>
                            <img src='{logoUrl}' alt='Ice Castle USA' style='max-width:180px;margin-bottom:10px;'/>
                            <h2 style='color:#fff;margin:0;'>New Customer Inquiry</h2>
                        </div>
                        <div style='padding:28px 24px 18px 24px;'>
                            <p><b>From:</b> {WebUtility.HtmlEncode(inquiry.UserEmail)}</p>
                            <p><b>Name:</b> {name}</p>
                            <p><b>Phone:</b> {phoneText}</p>
                            <p><b>VIN / Unit ID:</b> {vin}</p>
                            {tradeInHtml}
                            <div style='margin-top:18px;background:#fafafa;padding:12px;border-radius:8px;border:1px solid #eee;'>
                                <b>Message:</b><br>{message}
                            </div>
                        </div>
                        <div style='background:#222;color:#fff;padding:18px;text-align:center;font-size:14px;'>
                            <p style='margin:0;'><b>Forest Lake Auto Truck & Trailer Sales</b></p>
                            <p style='margin:0;'>{address}</p>
                            <p style='margin:0;'>{phone}</p>
                        </div>
                    </div>
                </body></html>";

                var salesMsg = new EmailMessage(
                    senderAddress: fromAddress,
                    content: new EmailContent($"New Inquiry from {name} ({inquiry.UserEmail})")
                    {
                        PlainText = $"New inquiry received:\n\nName: {inquiry.Name}\nEmail: {inquiry.UserEmail}\nPhone: {inquiry.Phone}\nVIN: {inquiry.Vin}\n\nMessage:\n{inquiry.Message}\n\nForest Lake Auto Truck & Trailer Sales",
                        Html = salesHtml
                    },
                    recipients: new EmailRecipients(new List<EmailAddress> { new EmailAddress(salesAddress) })
                );

                // Fire-and-forget: send both emails in the background
                Task.Run(() =>
                {
                    try { client.Send(WaitUntil.Completed, userMsg); } catch (Exception ex) { _logger.LogError(ex, "User email send failed"); }
                });
                Task.Run(() =>
                {
                    try { client.Send(WaitUntil.Completed, salesMsg); } catch (Exception ex) { _logger.LogError(ex, "Sales email send failed"); }
                });

                res.StatusCode = HttpStatusCode.Accepted;
                res.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await res.WriteStringAsync(JsonSerializer.Serialize(new
                {
                    ok = true,
                    message = "Inquiry received. Email delivery is processed asynchronously."
                }));
                return res;
            }
            catch (JsonException)
            {
                return await BadRequest(res, "Invalid JSON format.");
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Azure Email Service failed.");
                return await Error(res, $"Failed to send email: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in SendInquiry");
                return await Error(res, ex.Message);
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
