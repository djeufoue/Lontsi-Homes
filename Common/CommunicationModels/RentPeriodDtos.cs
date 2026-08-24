using System;
using System.Collections.Generic;
using Common.Enums;

namespace Common.CommunicationModels
{
    public class RentPeriodDto
    {
        public int Id { get; set; }
        public int TenancyId { get; set; }
        public DateTimeOffset PeriodStart { get; set; }
        public DateTimeOffset PeriodEnd { get; set; }
        public DateTimeOffset DueDate { get; set; }
        public int BillingGroupSequence { get; set; }
        public decimal Amount { get; set; }
        public decimal PaidAmount { get; set; }
        public DateTimeOffset? PaidDate { get; set; }
        public int? PaymentId { get; set; }
        public string PaymentReference { get; set; } = string.Empty;
        public string SystemReceiptNumber { get; set; } = string.Empty;
        public bool HasSystemReceipt { get; set; }
        public bool HasProviderReceipt { get; set; }
        public RentPeriodStatusEnum Status { get; set; }
        public string StatusLabel { get; set; } = string.Empty;
        public bool IsPayable { get; set; }
        public bool CanCancelPendingPayment { get; set; }
        public string LockedReason { get; set; } = string.Empty;
        public int ReminderCount { get; set; }
        public int ManualReminderCount { get; set; }
        public DateTimeOffset? LastReminderSentAt { get; set; }
        public List<RentReminderHistoryDto> ReminderHistory { get; set; } = new();
    }

    public class RentPeriodSeedDto
    {
        public DateTimeOffset PeriodStart { get; set; }
        public DateTimeOffset PeriodEnd { get; set; }
        public DateTimeOffset DueDate { get; set; }
        public int BillingGroupSequence { get; set; }
        public decimal Amount { get; set; }
        public RentPeriodStatusEnum Status { get; set; }
        public decimal PaidAmount { get; set; }
        public DateTimeOffset? PaidDate { get; set; }
    }

    public class TenantInvitationRequest
    {
        public string Email { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public string? CountryCode { get; set; }
        public string? PhoneNumber { get; set; }
        public string? WhatsAppPhoneNumber { get; set; }
        public TenancyMemberRoleEnum Role { get; set; } = TenancyMemberRoleEnum.MainTenant;
    }

    public class CreateGuidedTenancyRequest
    {
        public int ApartmentId { get; set; }
        public DateTimeOffset StartDate { get; set; }
        public DateTimeOffset? EndDate { get; set; }
        public decimal MonthlyRent { get; set; }
        public int MaxMembers { get; set; } = 1;
        public int RentDueDay { get; set; } = 1;
        public int PaymentIntervalMonths { get; set; } = 1;
        public TenancyEndBehaviorEnum EndBehavior { get; set; } = TenancyEndBehaviorEnum.NoEndDate;
        public int FutureRentPeriodCount { get; set; } = 1;
        public DateTimeOffset RentTrackingStartDate { get; set; }
        public List<RentPeriodSeedDto> RentPeriods { get; set; } = new();
        public TenantInvitationRequest MainTenant { get; set; } = new();
    }

    public class PayRentPeriodsRequest
    {
        public int TenancyId { get; set; }
        public int NumberOfPeriods { get; set; } = 1;
        public PaymentMethodEnum Method { get; set; } = PaymentMethodEnum.Card;
    }

    public class RentCheckoutSessionDto
    {
        public int PaymentId { get; set; }
        public int TenancyId { get; set; }
        public decimal RentAmount { get; set; }
        public string RentCurrency { get; set; } = "XAF";
        public decimal ChargeAmount { get; set; }
        public string ChargeCurrency { get; set; } = "USD";
        public PaymentMethodEnum PaymentMethod { get; set; } = PaymentMethodEnum.Card;
        public string PaymentReference { get; set; } = string.Empty;
        public string ProviderReference { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string PublishableKey { get; set; } = string.Empty;
        public string ReturnUrl { get; set; } = string.Empty;
        public string CheckoutUrl { get; set; } = string.Empty;
        public string Provider { get; set; } = "Stripe";
        public string PropertyName { get; set; } = string.Empty;
        public string ApartmentName { get; set; } = string.Empty;
        public string PeriodLabel { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    public class RentCheckoutStatusDto
    {
        public int PaymentId { get; set; }
        public int TenancyId { get; set; }
        public string PaymentReference { get; set; } = string.Empty;
        public string ProviderReference { get; set; } = string.Empty;
        public string PaymentStatus { get; set; } = string.Empty;
        public bool PaymentCompleted { get; set; }
        public string ReceiptNumber { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class TenantPaymentHistoryDto
    {
        public int PaymentId { get; set; }
        public int? TenancyId { get; set; }
        public DateTimeOffset PaymentDate { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;
        public PaymentMethodEnum Method { get; set; }
        public PaymentStatusEnum Status { get; set; }
        public string TransactionId { get; set; } = string.Empty;
        public string PeriodLabel { get; set; } = string.Empty;
        public string SystemReceiptNumber { get; set; } = string.Empty;
        public bool HasSystemReceipt { get; set; }
        public bool HasProviderReceipt { get; set; }
    }

    public class MarkRentPeriodPaidRequest
    {
        public DateTimeOffset? PaidDate { get; set; }
        public string? Note { get; set; }
    }

    public class MarkRentPeriodsPaidRequest
    {
        public List<int> RentPeriodIds { get; set; } = new();
        public DateTimeOffset? PaidDate { get; set; }
        public string? Note { get; set; }
    }

    public class ReconcileRentScheduleRequest
    {
        public int RentDueDay { get; set; }
        public int PaymentIntervalMonths { get; set; } = 1;
        public DateTimeOffset FirstTrackedPeriodStart { get; set; }
    }

    public class TenantDashboardTenancyDto
    {
        public TenancyDetailsDto Tenancy { get; set; } = new();
        public string LandlordName { get; set; } = string.Empty;
        public string LandlordEmail { get; set; } = string.Empty;
        public decimal OutstandingBalance { get; set; }
        public DateTimeOffset? NextDueDate { get; set; }
        public PaymentMethodEnum PaymentMethod { get; set; } = PaymentMethodEnum.Card;
        public bool CanPayRent { get; set; }
        public string PaymentUnavailableReason { get; set; } = string.Empty;
        public bool AutomaticPaymentsEnabled { get; set; }
        public List<RentPeriodDto> RentPeriods { get; set; } = new();
        public List<TenantPaymentHistoryDto> PaymentHistory { get; set; } = new();
        public List<DocumentDto> Documents { get; set; } = new();
    }

    public class TenantDashboardDto
    {
        public List<TenantDashboardTenancyDto> Tenancies { get; set; } = new();
    }

    public class TerminateTenancyRequest
    {
        public DateTimeOffset TerminationDate { get; set; }
        public TenancyTerminationReasonEnum Reason { get; set; } = TenancyTerminationReasonEnum.Other;
        public string? Notes { get; set; }
    }
}
