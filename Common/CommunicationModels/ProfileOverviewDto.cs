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
        public string? CountryIsoCode { get; set; }
        public string? PhoneNumber { get; set; }
        public bool UsePrimaryPhoneForSubscriptionPayments { get; set; }
        public string? SubscriptionPaymentPhoneNumber { get; set; }
        public PayoutChannelEnum? SubscriptionPaymentChannel { get; set; }
        public bool IsSubscriptionPaymentPhoneVerified { get; set; }
        public bool UsePrimaryPhoneForRentPayouts { get; set; }
        public string? PayoutPhoneNumber { get; set; }
        public PayoutChannelEnum? PayoutChannel { get; set; }
        public bool IsPayoutPhoneVerified { get; set; }
        public string? WhatsAppPhoneNumber { get; set; }
        public bool IsWhatsAppPhoneVerified { get; set; }
        public OtpRequestLimitDto? SubscriptionPaymentOtpRequestLimit { get; set; }
        public OtpRequestLimitDto? PayoutOtpRequestLimit { get; set; }
        public OtpRequestLimitDto? WhatsAppOtpRequestLimit { get; set; }
        public List<string> Roles { get; set; } = new();
        public bool IsSubscriptionExempt { get; set; }
        public PlatformLanguage Language { get; set; } = PlatformLanguage.English;
        public PlatformLanguage EmailLanguage { get; set; } = PlatformLanguage.English;
        public bool ConversationEmailNotificationsEnabled { get; set; } = true;
        public KycDocumentTypeEnum? KycDocumentType { get; set; }
        public LandlordKycStatusEnum KycStatus { get; set; } = LandlordKycStatusEnum.NotStarted;
        public bool IsKycSubmitted { get; set; }
        public bool IsKycApproved { get; set; }
        public DateTimeOffset? KycSubmittedAt { get; set; }
        public DateTimeOffset? KycReviewedAt { get; set; }
        public string? KycReviewNote { get; set; }
        public LandlordKycRejectedFilesDto KycRejectedFiles { get; set; } = new();
        public bool PlatformTermsAccepted { get; set; }
        public DateTimeOffset? PlatformTermsAcceptedAt { get; set; }
        public string? PlatformTermsSignatureName { get; set; }
        public string? PlatformTermsVersion { get; set; }
        public bool HasStripePayoutAccount { get; set; }
        public bool StripePayoutSetupStarted { get; set; }
        public bool StripePayoutSetupComplete { get; set; }
        public bool StripeConnectPlatformEnabled { get; set; } = true;
        public bool StripePayoutSetupRequired { get; set; } = true;
        public bool AutomaticPaymentsEnabled { get; set; }
        public string StripeConnectAccountId { get; set; } = string.Empty;
        public bool StripePayoutDetailsSubmitted { get; set; }
        public bool StripeChargesEnabled { get; set; }
        public bool StripePayoutsEnabled { get; set; }
        public string StripePayoutRequirementsSummary { get; set; } = string.Empty;
        public string StripePayoutDisabledReason { get; set; } = string.Empty;
        public DateTimeOffset? StripePayoutSetupStartedAt { get; set; }
        public DateTimeOffset? StripePayoutSetupCompletedAt { get; set; }
        public DateTimeOffset? StripePayoutStatusUpdatedAt { get; set; }
        public string NextOnboardingStep { get; set; } = LandlordOnboardingSteps.Complete;
        public bool CanStartSubscriptionCheckout { get; set; }
        public string SubscriptionBlockedReason { get; set; } = string.Empty;

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
