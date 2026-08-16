using System;
using System.Collections.Generic;

namespace Common.CommunicationModels
{
    public class PendingSubscriptionDto
    {
        public int Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string UserEmail { get; set; } = string.Empty;
        public string UserFullName { get; set; } = string.Empty;
        public int SubscriptionPlanId { get; set; }
        public string PlanName { get; set; } = string.Empty;
        public decimal PlanPrice { get; set; }
        public int PlanDurationInDays { get; set; }
        public int DurationMonths { get; set; }
        public DateTimeOffset StartDate { get; set; }
        public DateTimeOffset EndDate { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public bool IsApproved { get; set; }
        public bool IsDeleted { get; set; }
        public string Status { get; set; } = string.Empty;
        public string PaymentStatus { get; set; } = string.Empty;
        public string PaymentMethod { get; set; } = string.Empty;
        public string PaymentReference { get; set; } = string.Empty;
    }

    public class AdminSubscriptionHistoryDto
    {
        public PendingSubscriptionDto Subscription { get; set; } = new();
        public List<PendingSubscriptionDto> History { get; set; } = new();
    }

    public class ManualSubscriptionActivationRequest
    {
        [System.ComponentModel.DataAnnotations.Range(6, 36)]
        public int DurationMonths { get; set; } = 12;
    }
}
