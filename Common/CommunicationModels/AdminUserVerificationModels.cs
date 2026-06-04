using System;
using System.Collections.Generic;
using Common.Enums;

namespace Common.CommunicationModels
{
    public class AdminUserVerificationStatusDto
    {
        public string UserId { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string? CountryCode { get; set; }
        public string? PhoneNumber { get; set; }
        public bool EmailConfirmed { get; set; }
        public bool PhoneNumberConfirmed { get; set; }
        public string? SubscriptionPaymentPhoneNumber { get; set; }
        public PayoutChannelEnum? SubscriptionPaymentChannel { get; set; }
        public bool IsSubscriptionPaymentPhoneVerified { get; set; }
        public DateTimeOffset? SubscriptionPaymentPhoneVerifiedAt { get; set; }
        public string? PayoutPhoneNumber { get; set; }
        public PayoutChannelEnum? PayoutChannel { get; set; }
        public bool IsPayoutPhoneVerified { get; set; }
        public DateTimeOffset? PayoutPhoneVerifiedAt { get; set; }
        public string? WhatsAppPhoneNumber { get; set; }
        public bool IsWhatsAppPhoneVerified { get; set; }
        public DateTimeOffset? WhatsAppPhoneVerifiedAt { get; set; }
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
        public string NextOnboardingStep { get; set; } = LandlordOnboardingSteps.Account;
        public bool IsOnboardingComplete { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public List<string> Roles { get; set; } = new();
    }

    public class AdminUserOverviewDto
    {
        public AdminUserVerificationStatusDto User { get; set; } = new();
        public LandlordKycSummaryDto? Kyc { get; set; }
        public List<AdminUserOtpDto> OtpCodes { get; set; } = new();
        public List<AdminUserOnboardingStepDto> Steps { get; set; } = new();
    }

    public class AdminUserManagementPermissionsDto
    {
        public bool CanDeleteUsers { get; set; }
    }

    public class AdminUserOtpDto
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Channel { get; set; } = string.Empty;
        public string? Destination { get; set; }
        public string? Code { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public DateTimeOffset? UsedAt { get; set; }
        public bool HasCode { get; set; }
        public bool IsUsed { get; set; }
        public bool IsExpired { get; set; }
        public bool IsRequired { get; set; } = true;
        public int? RemainingSeconds { get; set; }
        public string Status { get; set; } = "not-generated";
        public string StatusLabel { get; set; } = "Not generated";
    }

    public class AdminUserOnboardingStepDto
    {
        public string Key { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string? Detail { get; set; }
        public bool IsComplete { get; set; }
        public bool IsCurrent { get; set; }
    }
}
