using System;
using Common.Enums;

namespace Common.CommunicationModels
{
    public class StripePayoutAccountStatusDto
    {
        public LandlordKycStatusEnum KycStatus { get; set; } = LandlordKycStatusEnum.NotStarted;
        public bool IsKycApproved => KycStatus == LandlordKycStatusEnum.Approved;
        public bool IsPlatformReady { get; set; } = true;
        public string PlatformReadinessMessage { get; set; } = string.Empty;
        public string AccountId { get; set; } = string.Empty;
        public string CountryIsoCode { get; set; } = string.Empty;
        public bool IsCountrySupported { get; set; } = true;
        public string CountryUnsupportedMessage { get; set; } = string.Empty;
        public bool HasAccount { get; set; }
        public bool DetailsSubmitted { get; set; }
        public bool ChargesEnabled { get; set; }
        public bool PayoutsEnabled { get; set; }
        public bool SetupComplete => HasAccount && DetailsSubmitted && ChargesEnabled && PayoutsEnabled;
        public string DisabledReason { get; set; } = string.Empty;
        public string RequirementsSummary { get; set; } = string.Empty;
        public DateTimeOffset? SetupStartedAt { get; set; }
        public DateTimeOffset? SetupCompletedAt { get; set; }
        public DateTimeOffset? StatusUpdatedAt { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class StripePayoutSetupLinkDto
    {
        public string AccountId { get; set; } = string.Empty;
        public string OnboardingUrl { get; set; } = string.Empty;
        public StripePayoutAccountStatusDto Status { get; set; } = new();
    }
}
