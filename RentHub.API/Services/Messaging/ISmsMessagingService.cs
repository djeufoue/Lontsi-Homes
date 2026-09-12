namespace RentHub.API.Services.Messaging;

public interface ISmsMessagingService
{
    Task<MessagingSendResult> SendAsync(
        string recipientPhoneNumberE164,
        string message,
        CancellationToken cancellationToken = default);
}
