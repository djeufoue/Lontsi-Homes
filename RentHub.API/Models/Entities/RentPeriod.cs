using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Common.Enums;
using Microsoft.EntityFrameworkCore;

namespace RentHub.API.Models.Entities
{
    [Index(nameof(TenancyId), nameof(PeriodStart), IsUnique = true)]
    [Index(nameof(TenancyId), nameof(Status), nameof(DueDate))]
    public class RentPeriod
    {
        [Key]
        public int Id { get; set; }

        public int TenancyId { get; set; }
        public Tenancy? Tenancy { get; set; }

        public DateTimeOffset PeriodStart { get; set; }
        public DateTimeOffset PeriodEnd { get; set; }
        public DateTimeOffset DueDate { get; set; }
        public int BillingGroupSequence { get; set; }

        [Column(TypeName = "decimal(14,2)")]
        public decimal Amount { get; set; }

        [Column(TypeName = "decimal(14,2)")]
        public decimal PaidAmount { get; set; }

        public DateTimeOffset? PaidDate { get; set; }

        public int? PaymentId { get; set; }
        public Payment? Payment { get; set; }

        public string PaymentReference { get; set; } = string.Empty;

        public RentPeriodStatusEnum Status { get; set; } = RentPeriodStatusEnum.NotDueYet;

        public bool IsDeleted { get; set; }
        public string? CreatedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string? UpdatedBy { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public string? DeletedBy { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }

        public ICollection<RentReminderTrigger> ReminderTriggers { get; set; } = new List<RentReminderTrigger>();
        public ICollection<RentReminderPeriod> ReminderPeriods { get; set; } = new List<RentReminderPeriod>();
    }
}
