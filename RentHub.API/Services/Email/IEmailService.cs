using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RentHub.API.Services.Email
{
    public class EmailAttachment
    {
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = "application/octet-stream";
        public byte[] Content { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Abstraction for sending email messages.  Implementations can integrate
    /// with SMTP servers or third-party providers.  Methods should not throw
    /// exceptions so that the application remains resilient.
    /// </summary>
    public interface IEmailService
    {
        /// <summary>
        /// Sends an email to the specified recipient.
        /// </summary>
        /// <param name="to">Recipient email address.</param>
        /// <param name="subject">Email subject.</param>
        /// <param name="body">Email body in plain text or HTML.</param>
        Task SendEmailAsync(string to, string subject, string body);

        /// <summary>
        /// Sends an email with optional file attachments.
        /// </summary>
        Task SendEmailAsync(string to, string subject, string body, IReadOnlyCollection<EmailAttachment> attachments);
    }
}
