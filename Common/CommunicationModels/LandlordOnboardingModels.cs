using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace Common.CommunicationModels
{
    public static class LandlordOnboardingSteps
    {
        public const string Account = "account";
        public const string Email = "email";
        public const string Phone = "phone";
        public const string MobilePayments = "mobile-payments";
        public const string MobilePaymentVerification = "mobile-payment-verification";
        public const string Kyc = "kyc";
        public const string Contract = "contract";
        public const string Complete = "complete";
    }

    public class StartLandlordRegistrationRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;

        [Required]
        [Display(Name = "First name")]
        public string FirstName { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Last name")]
        public string LastName { get; set; } = string.Empty;

        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Country code must contain only digits and may start with +.")]
        public string? CountryCode { get; set; }

        public int? PlanId { get; set; }
    }

    public class VerifyEmailOtpRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Email OTP")]
        [StringLength(6, MinimumLength = 4)]
        public string EmailOtp { get; set; } = string.Empty;
    }

    public class UpsertLandlordPhoneRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [RegularExpression(@"^\+?237$", ErrorMessage = "Only Cameroon country code +237 is supported for this verification step.")]
        public string? CountryCode { get; set; } = "+237";

        [Required]
        [Display(Name = "Phone number")]
        [RegularExpression(@"^6\d{8}$", ErrorMessage = "Phone number must be the 9-digit Cameroon number without +237, for example REMOVED_PRIVATE_VALUE.")]
        public string PhoneNumber { get; set; } = string.Empty;
    }

    public class VerifyPhoneOtpRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Phone OTP")]
        [StringLength(6, MinimumLength = 4)]
        public string PhoneOtp { get; set; } = string.Empty;
    }

    public class UpsertLandlordMobilePaymentsRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        public bool UsePrimaryPhoneForSubscriptionPayments { get; set; } = true;

        [Display(Name = "Subscription payment number")]
        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Subscription payment number must contain only digits and may start with +.")]
        public string? SubscriptionPaymentPhoneNumber { get; set; }

        [Display(Name = "Subscription payment operator")]
        public PayoutChannelEnum? SubscriptionPaymentChannel { get; set; }

        public bool UsePrimaryPhoneForRentPayouts { get; set; } = true;

        [Display(Name = "Rent payout number")]
        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Rent payout number must contain only digits and may start with +.")]
        public string? PayoutPhoneNumber { get; set; }

        [Display(Name = "Rent payout operator")]
        public PayoutChannelEnum? PayoutChannel { get; set; }

        [Display(Name = "WhatsApp number")]
        [RegularExpression(@"^\+?\d+$", ErrorMessage = "WhatsApp number must contain only digits and may start with +.")]
        public string? WhatsAppPhoneNumber { get; set; }
    }

    public class VerifyMobilePaymentPhonesRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Display(Name = "Subscription payment OTP")]
        [StringLength(6, MinimumLength = 4)]
        public string? SubscriptionPaymentOtp { get; set; }

        [Display(Name = "Rent payout OTP")]
        [StringLength(6, MinimumLength = 4)]
        public string? PayoutOtp { get; set; }

        [Display(Name = "WhatsApp OTP")]
        [StringLength(6, MinimumLength = 4)]
        public string? WhatsAppOtp { get; set; }
    }

    public class LandlordOnboardingStatusDto
    {
        public string UserId { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string? CountryCode { get; set; }
        public string? PhoneNumber { get; set; }
        public bool EmailConfirmed { get; set; }
        public bool PhoneNumberConfirmed { get; set; }
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
        public OtpRequestLimitDto? PhoneOtpRequestLimit { get; set; }
        public OtpRequestLimitDto? SubscriptionPaymentOtpRequestLimit { get; set; }
        public OtpRequestLimitDto? PayoutOtpRequestLimit { get; set; }
        public OtpRequestLimitDto? WhatsAppOtpRequestLimit { get; set; }
        public string NextStep { get; set; } = LandlordOnboardingSteps.Account;
        public bool IsComplete { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public List<string> Roles { get; set; } = new();
    }

    public class OtpRequestLimitDto
    {
        public int DailyRequestLimit { get; set; }
        public int DailyRequestsRemaining { get; set; }
        public int RetryAfterSeconds { get; set; }
        public bool DailyLimitReached { get; set; }
        public DateTimeOffset? NextAllowedAt { get; set; }
        public DateTimeOffset? DailyLimitResetsAt { get; set; }
    }
}
