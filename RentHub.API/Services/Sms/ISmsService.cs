using System.Threading.Tasks;

namespace RentHub.API.Services.Sms
{
    /// <summary>
    /// Abstraction for sending SMS messages.  Concrete implementations can
    /// integrate with providers such as Twilio.  Methods should return
    /// completed tasks even if sending fails to avoid blocking.
    /// </summary>
    public interface ISmsService
    {
        /// <summary>
        /// Sends an SMS message to the specified phone number.  Implementations
        /// should format the number appropriately for the SMS provider.
        /// </summary>
        /// <param name="to">The recipient phone number including country code.</param>
        /// <param name="message">The body of the SMS message.</param>
        Task SendSmsAsync(string to, string message);

        /// <summary>
        /// Sends an SMS and reports whether the provider accepted the delivery attempt.
        /// Audited workflows must use this method instead of assuming that a no-op succeeded.
        /// </summary>
        Task<SmsSendResult> TrySendSmsAsync(string to, string message);

        /// <summary>
        /// Sends a WhatsApp message to the specified phone number.
        /// </summary>
        /// <param name="to">The recipient phone number including country code.</param>
        /// <param name="message">The message body.</param>
        Task SendWhatsAppAsync(string to, string message);
    }

    public sealed class SmsSendResult
    {
        public bool Succeeded { get; init; }
        public string Error { get; init; } = string.Empty;

        public static SmsSendResult Success() => new() { Succeeded = true };
        public static SmsSendResult Failure(string error) => new() { Succeeded = false, Error = error };
    }
}
