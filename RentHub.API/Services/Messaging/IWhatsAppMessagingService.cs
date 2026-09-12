namespace RentHub.API.Services.Messaging;

public sealed record WhatsAppTemplateMessage(
    string RecipientPhoneNumberE164,
    string TemplateName,
    string LanguageCode,
    IReadOnlyList<string> BodyPlaceholders,
    IReadOnlyList<string>? UrlButtonParameters = null,
    string? ClientMessageId = null,
    WhatsAppDocumentHeader? Document = null);

public sealed record WhatsAppDocumentHeader(string MediaUrl, string Filename);

/// <summary>
/// Sends approved templates only. There is intentionally no free-form WhatsApp API.
/// </summary>
public interface IWhatsAppMessagingService
{
    Task<MessagingSendResult> SendTemplateAsync(
        WhatsAppTemplateMessage message,
        CancellationToken cancellationToken = default);
}
