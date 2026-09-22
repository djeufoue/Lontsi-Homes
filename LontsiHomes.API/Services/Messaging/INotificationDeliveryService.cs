namespace LontsiHomes.API.Services.Messaging;

public sealed record EnqueueWhatsAppNotification(
    string EventType,
    string RecipientUserId,
    IReadOnlyList<string> BodyPlaceholders,
    string IdempotencyKey,
    IReadOnlyList<string>? UrlButtonParameters = null,
    string? RelatedEntityId = null,
    WhatsAppDocumentHeader? Document = null,
    WhatsAppReceiptReference? ReceiptDocument = null);

public sealed record WhatsAppReceiptReference(int PaymentId, string VerificationCode, string ReceiptNumber);

public interface INotificationDeliveryService
{
    Task<long?> EnqueueWhatsAppAsync(
        EnqueueWhatsAppNotification notification,
        CancellationToken cancellationToken = default);

    Task ProcessPendingAsync(CancellationToken cancellationToken = default);
}
