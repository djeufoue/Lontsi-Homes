using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace RentHub.API.Models.Entities
{
    [Index(nameof(Provider), nameof(EventKey), IsUnique = true)]
    [Index(nameof(PaymentReference))]
    public class PaymentWebhookEvent
    {
        [Key]
        public int Id { get; set; }
        public string Provider { get; set; } = string.Empty;
        public string EventKey { get; set; } = string.Empty;
        public string? EventType { get; set; }
        public string? PaymentReference { get; set; }
        public string? ProviderTransactionId { get; set; }
        public string? PayloadHash { get; set; }
        public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? ProcessedAt { get; set; }
        public string ProcessingStatus { get; set; } = "Received";
        public string? ProcessingMessage { get; set; }
    }
}
