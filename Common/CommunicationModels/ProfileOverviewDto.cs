using System;
using System.Collections.Generic;
using Common.Enums;

namespace Common.CommunicationModels
{
    public class ProfileOverviewDto
    {
        public string UserId { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string? CountryCode { get; set; }
        public string? PhoneNumber { get; set; }
        public string? PayoutPhoneNumber { get; set; }
        public PayoutChannelEnum? PayoutChannel { get; set; }
        public bool IsPayoutPhoneVerified { get; set; }
        public string? WhatsAppPhoneNumber { get; set; }
        public bool IsWhatsAppPhoneVerified { get; set; }
        public List<string> Roles { get; set; } = new();

        public int PropertyCount { get; set; }
        public int ApartmentCount { get; set; }

        public bool HasActiveSubscription { get; set; }
        public bool SubscriptionApproved { get; set; }
        public int? CurrentPlanId { get; set; }
        public string CurrentPlanName { get; set; } = string.Empty;
        public decimal? CurrentPlanPrice { get; set; }
        public int? CurrentPlanDurationInDays { get; set; }
        public DateTimeOffset? SubscriptionStartDate { get; set; }
        public DateTimeOffset? SubscriptionEndDate { get; set; }

        public int? PendingSubscriptionId { get; set; }
        public int? PendingPlanId { get; set; }
        public string PendingPlanName { get; set; } = string.Empty;
        public decimal? PendingPlanPrice { get; set; }
        public PaymentStatusEnum? PendingPaymentStatus { get; set; }
        public PaymentMethodEnum? PendingPaymentMethod { get; set; }
        public string PendingPaymentReference { get; set; } = string.Empty;

        public List<ProfilePropertyDto> Properties { get; set; } = new();
        public List<SubscriptionPlanDto> AvailablePlans { get; set; } = new();
    }

    public class ProfilePropertyDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public int ApartmentCount { get; set; }
        public string AccessSource { get; set; } = string.Empty;
    }
}
