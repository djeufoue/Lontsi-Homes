namespace RentHub.API.Models.Entities;

public sealed class NotificationDelivery
{
    public long Id { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? RelatedEntityId { get; set; }
    public string TemplateName { get; set; } = string.Empty;
    public string TemplateLanguage { get; set; } = string.Empty;
    public string RecipientUserId { get; set; } = string.Empty;
    public ApplicationUser? RecipientUser { get; set; }
    public string RecipientPhoneNumberE164 { get; set; } = string.Empty;
    public string Status { get; set; } = NotificationDeliveryStatuses.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? LastErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;

    // Stored alongside the outbox metadata so a retry uses exactly the approved,
    // structured business values captured when the domain event occurred.
    public string PayloadJson { get; set; } = "[]";
    public string ButtonPayloadsJson { get; set; } = "[]";
}
