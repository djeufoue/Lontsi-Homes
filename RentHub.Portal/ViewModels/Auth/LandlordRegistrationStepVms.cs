using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Common.CommunicationModels;
using Common.Enums;
using Microsoft.AspNetCore.Http;

namespace RentHub.Portal.ViewModels.Auth
{
    public class LandlordOnboardingProgressVm
    {
        public string CurrentStep { get; set; } = LandlordOnboardingSteps.Account;
        public string? CountryCode { get; set; }
        public string? CountryIsoCode { get; set; }
        public bool SmsVerificationTemporarilyUnavailable { get; set; } = true;

        public IReadOnlyList<string> Steps
        {
            get
            {
                var steps = new List<string>
                {
                    LandlordOnboardingSteps.Account,
                    LandlordOnboardingSteps.Email,
                    LandlordOnboardingSteps.Country,
                    LandlordOnboardingSteps.Phone
                };

                if (!SmsVerificationTemporarilyUnavailable && IncludeCameroonMobileMoneySteps)
                {
                    steps.Add(LandlordOnboardingSteps.MobilePayments);
                    steps.Add(LandlordOnboardingSteps.MobilePaymentVerification);
                }

                steps.Add(LandlordOnboardingSteps.Kyc);
                steps.Add(LandlordOnboardingSteps.Contract);
                return steps;
            }
        }

        public int CurrentStepNumber
        {
            get
            {
                var index = Steps
                    .Select((step, position) => new { step, position })
                    .FirstOrDefault(item => string.Equals(item.step, CurrentStep, StringComparison.OrdinalIgnoreCase))
                    ?.position;

                return (index ?? 0) + 1;
            }
        }

        public int TotalSteps => Steps.Count;

        public string KickerText => $"Step {CurrentStepNumber} of {TotalSteps}";

        public bool IncludeCameroonMobileMoneySteps =>
            IsCameroonCountry(CountryIsoCode, CountryCode) ||
            string.Equals(CurrentStep, LandlordOnboardingSteps.MobilePayments, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(CurrentStep, LandlordOnboardingSteps.MobilePaymentVerification, StringComparison.OrdinalIgnoreCase);

        private static bool IsCameroonCountry(string? countryIsoCode, string? countryCode)
        {
            if (string.Equals((countryIsoCode ?? string.Empty).Trim(), "CM", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var normalized = string.IsNullOrWhiteSpace(countryCode) ? "+1" : countryCode.Trim();
            if (!normalized.StartsWith('+'))
            {
                normalized = $"+{normalized}";
            }

            return string.Equals(normalized, "+237", StringComparison.Ordinal);
        }
    }

    public class VerifyLandlordEmailVm
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Email OTP")]
        [StringLength(6, MinimumLength = 4)]
        public string EmailOtp { get; set; } = string.Empty;

        public string? ReturnUrl { get; set; }
        public LandlordOnboardingStatusDto? Status { get; set; }
    }

    public class VerifyLandlordPhoneVm
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Display(Name = "Country code")]
        [Required]
        [RegularExpression(@"^\+?\d{1,4}$", ErrorMessage = "Choose a valid country code.")]
        public string? CountryCode { get; set; }

        [Required]
        [Display(Name = "Phone number")]
        [RegularExpression(@"^\+?\d[\d\s().-]{5,24}$", ErrorMessage = "Enter a valid phone number for the selected country.")]
        public string PhoneNumber { get; set; } = string.Empty;

        [Display(Name = "Phone OTP")]
        [StringLength(6, MinimumLength = 4)]
        public string? PhoneOtp { get; set; }

        public string? ReturnUrl { get; set; }
        public LandlordOnboardingStatusDto? Status { get; set; }
    }

    public class LandlordCountryVm
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Country")]
        [RegularExpression(@"^[A-Za-z]{2}$", ErrorMessage = "Choose a valid country.")]
        public string CountryIsoCode { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Country code")]
        [RegularExpression(@"^\+?\d{1,4}$", ErrorMessage = "Choose a valid country code.")]
        public string CountryCode { get; set; } = string.Empty;

        public string? ReturnUrl { get; set; }
        public LandlordOnboardingStatusDto? Status { get; set; }
    }

    public class LandlordMobilePaymentsVm
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Display(Name = "Use my verified phone for subscription payments")]
        public bool UsePrimaryPhoneForSubscriptionPayments { get; set; } = true;

        [Display(Name = "Subscription payment number")]
        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Subscription payment number must contain only digits and may start with +.")]
        public string? SubscriptionPaymentPhoneNumber { get; set; }

        [Display(Name = "Subscription payment operator")]
        [Required]
        public PayoutChannelEnum? SubscriptionPaymentChannel { get; set; }

        [Display(Name = "Use my verified phone for rent payouts")]
        public bool UsePrimaryPhoneForRentPayouts { get; set; } = true;

        [Display(Name = "Rent payout number")]
        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Rent payout number must contain only digits and may start with +.")]
        public string? PayoutPhoneNumber { get; set; }

        [Display(Name = "Rent payout operator")]
        [Required]
        public PayoutChannelEnum? PayoutChannel { get; set; }

        [Display(Name = "WhatsApp alerts number")]
        [RegularExpression(@"^\+?\d+$", ErrorMessage = "WhatsApp number must contain only digits and may start with +.")]
        public string? WhatsAppPhoneNumber { get; set; }

        public string? PrimaryPhoneNumber { get; set; }
        public string? ReturnUrl { get; set; }
        public LandlordOnboardingStatusDto? Status { get; set; }
    }

    public class VerifyLandlordMobilePaymentsVm
    {
        [Required, EmailAddress]
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

        public string? ReturnUrl { get; set; }
        public LandlordOnboardingStatusDto? Status { get; set; }
    }

    public class LandlordKycVm
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Display(Name = "Document type")]
        [Required]
        public KycDocumentTypeEnum? DocumentType { get; set; }

        [Display(Name = "Face photo - front")]
        public IFormFile? FaceFront { get; set; }

        [Display(Name = "Face photo - looking right")]
        public IFormFile? FaceRight { get; set; }

        [Display(Name = "Face photo - looking left")]
        public IFormFile? FaceLeft { get; set; }

        [Display(Name = "Document front")]
        public IFormFile? DocumentFront { get; set; }

        [Display(Name = "Document back")]
        public IFormFile? DocumentBack { get; set; }

        public string? ReturnUrl { get; set; }
        public LandlordOnboardingStatusDto? Status { get; set; }
    }

    public class LandlordContractVm
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Display(Name = "I accept the platform terms and conditions")]
        public bool Accepted { get; set; }

        [Required]
        [Display(Name = "Full legal name")]
        [StringLength(160, MinimumLength = 2)]
        public string SignatureName { get; set; } = string.Empty;

        public string? ReturnUrl { get; set; }
        public LandlordOnboardingStatusDto? Status { get; set; }
    }
}
