namespace Common.CommunicationModels;

public sealed record BeginWhatsAppVerificationRequest(
    bool TransactionalConsentAccepted,
    bool UsePrimaryPhoneNumber,
    string? PhoneNumber);

public sealed record VerifyWhatsAppNumberRequest(string? Code);

public sealed record WhatsAppPreferenceDto(
    string State,
    string? PhoneNumber,
    string? PendingPhoneNumber,
    bool UsesPrimaryPhoneNumber,
    bool IsVerified,
    bool HasTransactionalConsent,
    string ConsentTextVersion);
