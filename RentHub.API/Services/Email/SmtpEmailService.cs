using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;

namespace RentHub.API.Services.Email
{
    public class SmtpEmailService : IEmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<SmtpEmailService> _logger;

        public SmtpEmailService(IConfiguration configuration, ILogger<SmtpEmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task SendEmailAsync(string to, string subject, string body)
        {
            await SendEmailAsync(to, subject, body, Array.Empty<EmailAttachment>());
        }

        public async Task SendEmailAsync(string to, string subject, string body, IReadOnlyCollection<EmailAttachment> attachments)
        {
            try
            {
                var host = _configuration["Email:Smtp:Host"];
                if (string.IsNullOrWhiteSpace(host))
                {
                    _logger.LogWarning("SMTP host is not configured. OTP email to {Recipient} was skipped.", to);
                    return;
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
                    _logger.LogWarning("SMTP sender is not configured. OTP email to {Recipient} was skipped.", to);
                    return;
                }

                var senderName = string.IsNullOrWhiteSpace(fromName) ? "Lontsi Homes" : fromName;
                using var message = new MailMessage(new MailAddress(fromAddress, senderName), new MailAddress(to))
                {
                    Subject = subject,
                    Body = body,
                    IsBodyHtml = false
                };

                foreach (var attachment in attachments.Where(a => a.Content.Length > 0 && !string.IsNullOrWhiteSpace(a.FileName)))
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {Recipient}.", to);
            }
        }
    }
}
