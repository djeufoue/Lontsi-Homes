using System.ComponentModel.DataAnnotations;
using Common.Enums;
using Microsoft.EntityFrameworkCore;

namespace RentHub.API.Models.Entities
{
    /// <summary>
    /// Links a user to a subscription plan with start and end dates. When EndDate
    /// expires the user loses features until they renew.
    /// </summary>
    [Index(nameof(PaymentReference), IsUnique = true)]
    [Index(nameof(PaymentProviderTransactionId), IsUnique = true)]
    public class UserSubscription
    {
        [Key]
        public int Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public ApplicationUser? User { get; set; }
        public int SubscriptionPlanId { get; set; }
        public SubscriptionPlan? SubscriptionPlan { get; set; }
        public DateTimeOffset StartDate { get; set; }
        public DateTimeOffset EndDate { get; set; }

        // Plan snapshot captured at subscription creation to avoid plan updates
        // changing active subscriptions already owned by landlords.
        public string PlanNameSnapshot { get; set; } = string.Empty;
        public decimal PlanPriceSnapshot { get; set; }
        public int PlanDurationInDaysSnapshot { get; set; }
        public int? PlanMaxPropertiesSnapshot { get; set; }
        public int? PlanMaxApartmentsPerPropertySnapshot { get; set; }
        public PaymentMethodEnum? PaymentMethod { get; set; }
        public PaymentStatusEnum PaymentStatus { get; set; } = PaymentStatusEnum.Pending;
        public string PaymentReference { get; set; } = string.Empty;
        public string? PaymentProviderTransactionId { get; set; }
        public string? PaymentAuthorizationUrl { get; set; }
        public bool AllowAutomaticCardPayments { get; set; }
        public DateTimeOffset? PaymentCompletedAt { get; set; }
        public int PaymentAttemptCount { get; set; }

        /// <summary>
        /// Indicates whether the subscription has been approved by an administrator. Landlords
        /// cannot add owners, managers or tenants until their subscription has been approved.
        /// </summary>
        public bool IsApproved { get; set; } = false;

        // Audit fields
        public bool IsDeleted { get; set; } = false;
        public string? CreatedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string? UpdatedBy { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public string? DeletedBy { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
    }
}
