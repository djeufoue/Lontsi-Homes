using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LontsiHomes.API.Services.Email
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

        /// <summary>
        /// Sends an email and returns a delivery-attempt result. This is used by audited
        /// workflows such as rent reminders, where silently swallowing a provider error
        /// would create an incorrect history entry.
        /// </summary>
        Task<EmailSendResult> TrySendEmailAsync(EmailMessage email);
    }

    public sealed class EmailMessage
    {
        public string To { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string PlainTextBody { get; set; } = string.Empty;
        public string HtmlBody { get; set; } = string.Empty;
        public IReadOnlyCollection<EmailAttachment> Attachments { get; set; } = Array.Empty<EmailAttachment>();
    }

    public sealed class EmailSendResult
    {
        public bool Succeeded { get; init; }
        public string Error { get; init; } = string.Empty;

        public static EmailSendResult Success() => new() { Succeeded = true };
        public static EmailSendResult Failure(string error) => new() { Succeeded = false, Error = error };
    }
}
