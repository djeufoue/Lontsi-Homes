using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;

namespace LontsiHomes.API.Services.Email
{
    public class SmtpEmailService : IEmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<SmtpEmailService> _logger;
        private readonly IWebHostEnvironment _environment;

        public SmtpEmailService(
            IConfiguration configuration,
            ILogger<SmtpEmailService> logger,
            IWebHostEnvironment environment)
        {
            _configuration = configuration;
            _logger = logger;
            _environment = environment;
        }

        public async Task SendEmailAsync(string to, string subject, string body)
        {
            await TrySendEmailAsync(new EmailMessage
            {
                To = to,
                Subject = subject,
                PlainTextBody = body
            });
        }

        public async Task SendEmailAsync(string to, string subject, string body, IReadOnlyCollection<EmailAttachment> attachments)
        {
            await TrySendEmailAsync(new EmailMessage
            {
                To = to,
                Subject = subject,
                PlainTextBody = body,
                Attachments = attachments
            });
        }

        public async Task<EmailSendResult> TrySendEmailAsync(EmailMessage email)
        {
            try
            {
                var host = _configuration["Email:Smtp:Host"];
                if (string.IsNullOrWhiteSpace(host))
                {
                    const string error = "SMTP host is not configured.";
                    _logger.LogWarning("{Error} Email to {Recipient} was skipped.", error, email.To);
                    return EmailSendResult.Failure(error);
                }

                var portRaw = _configuration["Email:Smtp:Port"];
                var username = _configuration["Email:Smtp:Username"];
                var password = _configuration["Email:Smtp:Password"];
                var from = _configuration["Email:Smtp:From"];
                var fromName = _configuration["Email:Smtp:FromName"];
                var useSslRaw = _configuration["Email:Smtp:UseSsl"];

                var port = int.TryParse(portRaw, out var parsedPort) ? parsedPort : 587;
                var useSsl = bool.TryParse(useSslRaw, out var parsedSsl) ? parsedSsl : true;

                var fromAddress = string.IsNullOrWhiteSpace(from) ? username : from;
                if (string.IsNullOrWhiteSpace(fromAddress))
                {
                    const string error = "SMTP sender is not configured.";
                    _logger.LogWarning("{Error} Email to {Recipient} was skipped.", error, email.To);
                    return EmailSendResult.Failure(error);
                }

                var senderName = string.IsNullOrWhiteSpace(fromName) ? "Lontsi Homes" : fromName;
                using var message = new MailMessage(new MailAddress(fromAddress, senderName), new MailAddress(email.To))
                {
                    Subject = email.Subject,
                    Body = email.PlainTextBody,
                    IsBodyHtml = false
                };

                message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                    email.PlainTextBody,
                    null,
                    MediaTypeNames.Text.Plain));

                var logoPath = ResolveBrandLogoPath();
                var htmlBody = BuildBrandedHtml(
                    string.IsNullOrWhiteSpace(email.HtmlBody) ? email.PlainTextBody : email.HtmlBody,
                    logoPath is not null,
                    !string.IsNullOrWhiteSpace(email.HtmlBody));
                var htmlView = AlternateView.CreateAlternateViewFromString(
                    htmlBody,
                    null,
                    MediaTypeNames.Text.Html);

                if (logoPath is not null)
                {
                    var logo = new LinkedResource(logoPath, MediaTypeNames.Image.Png)
                    {
                        ContentId = "lontsi-homes-logo",
                        TransferEncoding = TransferEncoding.Base64
                    };
                    htmlView.LinkedResources.Add(logo);
                }

                message.AlternateViews.Add(htmlView);

                foreach (var attachment in email.Attachments.Where(a => a.Content.Length > 0 && !string.IsNullOrWhiteSpace(a.FileName)))
                {
                    var stream = new MemoryStream(attachment.Content);
                    message.Attachments.Add(new Attachment(
                        stream,
                        attachment.FileName,
                        string.IsNullOrWhiteSpace(attachment.ContentType)
                            ? "application/octet-stream"
                            : attachment.ContentType));
                }

                using var client = new SmtpClient(host, port)
                {
                    EnableSsl = useSsl,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    UseDefaultCredentials = false
                };

                if (!string.IsNullOrWhiteSpace(username))
                {
                    client.Credentials = new NetworkCredential(username, password ?? string.Empty);
                }

                await client.SendMailAsync(message);
                return EmailSendResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {Recipient}.", email.To);
                return EmailSendResult.Failure(ex.Message);
            }
        }

        private string? ResolveBrandLogoPath()
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "assets", "branding", "logo-email.png"),
                Path.Combine(_environment.ContentRootPath, "assets", "branding", "logo-email.png"),
                Path.GetFullPath(Path.Combine(
                    _environment.ContentRootPath,
                    "..",
                    "LontsiHomes.Portal",
                    "wwwroot",
                    "assets",
                    "branding",
                    "logo-email.png"))
            };

            var path = candidates.FirstOrDefault(File.Exists);
            if (path is null)
            {
                _logger.LogWarning("The Lontsi Homes email logo could not be found. A text brand fallback will be used.");
            }

            return path;
        }

        private static string BuildBrandedHtml(string body, bool hasLogo, bool bodyIsHtml)
        {
            var safeBody = bodyIsHtml
                ? body ?? string.Empty
                : WebUtility.HtmlEncode(body ?? string.Empty)
                    .Replace("\r\n", "<br />", StringComparison.Ordinal)
                    .Replace("\n", "<br />", StringComparison.Ordinal);
            var brand = hasLogo
                ? "<img src=\"cid:lontsi-homes-logo\" width=\"300\" alt=\"Lontsi Homes\" style=\"display:block;width:100%;max-width:300px;height:auto;margin:0 auto;border:0;outline:none;text-decoration:none;\" />"
                : "<div style=\"color:#ffffff;font-size:25px;font-weight:800;letter-spacing:-0.02em;text-align:center;\">Lontsi <span style=\"color:#8A9F3C;\">Homes</span></div>";

            return $$"""
                <!doctype html>
                <html>
                <head>
                  <meta charset="utf-8" />
                  <meta name="viewport" content="width=device-width, initial-scale=1" />
                  <meta name="color-scheme" content="light dark" />
                  <meta name="supported-color-schemes" content="light dark" />
                  <style>
                    :root { color-scheme: light dark; supported-color-schemes: light dark; }
                    @media (prefers-color-scheme: dark) {
                      .lh-email-page { background:#171a18 !important; }
                      .lh-email-card { background:#242826 !important; border-color:#394039 !important; }
                      .lh-email-copy { color:#f4f3ed !important; }
                      .lh-email-footer { color:#b9bdb6 !important; }
                    }
                  </style>
                </head>
                <body class="lh-email-page" style="margin:0;padding:0;background:#f7f6ef;font-family:Arial,'Segoe UI',sans-serif;">
                  <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" style="width:100%;background:#f7f6ef;">
                    <tr>
                      <td align="center" style="padding:24px 12px;">
                        <table class="lh-email-card" role="presentation" width="640" cellspacing="0" cellpadding="0" border="0" style="width:100%;max-width:640px;background:#ffffff;border:1px solid #e2e4dc;border-radius:18px;overflow:hidden;">
                          <tr>
                            <td align="center" style="padding:24px;background:#1f2522;">{{brand}}</td>
                          </tr>
                          <tr>
                            <td class="lh-email-copy" style="padding:32px 28px;color:#1f2522;font-size:16px;line-height:1.65;">{{safeBody}}</td>
                          </tr>
                          <tr>
                            <td class="lh-email-footer" align="center" style="padding:18px 24px;border-top:1px solid #e2e4dc;color:#6b7280;font-size:12px;line-height:1.5;">
                              Lontsi Homes &middot; Rent &middot; Manage &middot; Grow
                            </td>
                          </tr>
                        </table>
                      </td>
                    </tr>
                  </table>
                </body>
                </html>
                """;
        }
    }
}
