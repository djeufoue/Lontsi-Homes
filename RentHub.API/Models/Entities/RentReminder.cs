using Common.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RentHub.API.Models.Entities
{
    public class RentReminder
    {
        [Key]
        public int Id { get; set; }

        public int TenancyId { get; set; }
        public Tenancy? Tenancy { get; set; }

        public RentReminderCategoryEnum Category { get; set; }
        public bool IsManual { get; set; }
        public DateTimeOffset ScheduledFor { get; set; }
        public RentReminderStatusEnum Status { get; set; } = RentReminderStatusEnum.Pending;

        [Column(TypeName = "decimal(14,2)")]
        public decimal OutstandingAmountSnapshot { get; set; }

        public int IncludedPeriodCount { get; set; }

        [MaxLength(320)]
        public string RecipientEmail { get; set; } = string.Empty;

        [MaxLength(64)]
        public string RecipientPhone { get; set; } = string.Empty;

        [MaxLength(300)]
        public string Subject { get; set; } = string.Empty;

        public string PlainTextBody { get; set; } = string.Empty;
        public string HtmlBody { get; set; } = string.Empty;

        public ReminderDeliveryStatusEnum EmailStatus { get; set; } = ReminderDeliveryStatusEnum.NotRequested;
        public ReminderDeliveryStatusEnum SmsStatus { get; set; } = ReminderDeliveryStatusEnum.NotRequested;
        public bool WhatsAppRequested { get; set; }
        public int EmailAttemptCount { get; set; }
        public int SmsAttemptCount { get; set; }
        public DateTimeOffset? SentAt { get; set; }
        public DateTimeOffset? InvalidatedAt { get; set; }

        [MaxLength(512)]
        public string InvalidationReason { get; set; } = string.Empty;

        [MaxLength(2048)]
        public string FailureReason { get; set; } = string.Empty;

        [MaxLength(450)]
        public string? RequestedByUserId { get; set; }

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? UpdatedAt { get; set; }

        public ICollection<RentReminderTrigger> Triggers { get; set; } = new List<RentReminderTrigger>();
        public ICollection<RentReminderPeriod> Periods { get; set; } = new List<RentReminderPeriod>();
    }
}
