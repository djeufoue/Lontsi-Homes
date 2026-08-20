using System.Collections.Generic;
using System.Threading.Tasks;

namespace RentHub.API.Services.Email
{
    /// <summary>
    /// Basic no-op email service used when no real email integration is available.  This
    /// implementation simply completes without sending.  In production, replace
    /// this with an integration to an SMTP server or third-party provider.
    /// </summary>
    public class StubEmailService : IEmailService
    {
        public async Task SendEmailAsync(string to, string subject, string body)
        {
            // Intentionally do nothing.  For demonstration, emails are not sent.
            await Task.CompletedTask;
        }

        public async Task SendEmailAsync(string to, string subject, string body, IReadOnlyCollection<EmailAttachment> attachments)
        {
            // Intentionally do nothing.  For demonstration, emails are not sent.
            await Task.CompletedTask;
        }

        public Task<EmailSendResult> TrySendEmailAsync(EmailMessage email)
        {
            // The stub represents a successful local delivery so idempotent workflows can
            // be exercised without repeatedly retrying a provider that is intentionally absent.
            return Task.FromResult(EmailSendResult.Success());
        }
    }
}
