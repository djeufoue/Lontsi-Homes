namespace RentHub.API.Services.Messaging;

public sealed record MessagingSendResult(
    bool Succeeded,
    string? ProviderMessageId = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static MessagingSendResult Success(string? providerMessageId) =>
        new(true, providerMessageId);

    public static MessagingSendResult Failure(string errorCode, string errorMessage) =>
        new(false, null, errorCode, errorMessage);
}
