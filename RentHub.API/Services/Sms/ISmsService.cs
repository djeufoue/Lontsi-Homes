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
        /// Sends a WhatsApp message to the specified phone number.
        /// </summary>
        /// <param name="to">The recipient phone number including country code.</param>
        /// <param name="message">The message body.</param>
        Task SendWhatsAppAsync(string to, string message);
    }
}
