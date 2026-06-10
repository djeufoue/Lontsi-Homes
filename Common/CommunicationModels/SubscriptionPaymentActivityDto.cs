using System;
using System.Collections.Generic;

namespace Common.CommunicationModels
{
    public class SubscriptionPaymentActivityResponseDto
    {
        public List<SubscriptionPaymentActivityDto> Items { get; set; } = new();
        public SubscriptionPaymentActivitySummaryDto Summary { get; set; } = new();
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
        public string Search { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    public class SubscriptionPaymentActivitySummaryDto
    {
        public int TotalTransactions { get; set; }
        public int SuccessfulTransactions { get; set; }
        public int FailedTransactions { get; set; }
        public int PendingTransactions { get; set; }
        public int AutomaticRenewals { get; set; }
        public decimal TotalSuccessfulUsd { get; set; }
        public decimal TotalSuccessfulXaf { get; set; }
    }

    public class SubscriptionPaymentActivityDto
    {
        public int SubscriptionId { get; set; }
        public int PlanId { get; set; }
        public string PlanName { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string UserEmail { get; set; } = string.Empty;
        public string UserFullName { get; set; } = string.Empty;
        public int? PropertyId { get; set; }
        public string PropertyName { get; set; } = string.Empty;
        public string PaymentReference { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public string ProviderReference { get; set; } = string.Empty;
        public string PaymentMethod { get; set; } = string.Empty;
        public string PaymentStatus { get; set; } = string.Empty;
        public decimal AmountUsd { get; set; }
        public decimal AmountXaf { get; set; }
        public string Currency { get; set; } = string.Empty;
        public bool AllowAutomaticCardPayments { get; set; }
        public bool IsAutomaticRenewal { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? PaymentCompletedAt { get; set; }
        public DateTimeOffset StartDate { get; set; }
        public DateTimeOffset EndDate { get; set; }
        public string ProcessingMessage { get; set; } = string.Empty;
    }
}
