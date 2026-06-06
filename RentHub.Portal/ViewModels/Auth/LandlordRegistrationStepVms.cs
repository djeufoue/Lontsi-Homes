using System.ComponentModel.DataAnnotations;
using Common.CommunicationModels;
using Common.Enums;
using Microsoft.AspNetCore.Http;

namespace RentHub.Portal.ViewModels.Auth
{
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
        [RegularExpression(@"^\+?237$", ErrorMessage = "Only Cameroon country code +237 is supported for this verification step.")]
        public string? CountryCode { get; set; } = "+237";

        [Required]
        [Display(Name = "Phone number")]
        [RegularExpression(@"^6\d{8}$", ErrorMessage = "Enter the 9-digit Cameroon phone number without +237, for example REMOVED_PRIVATE_VALUE.")]
        public string PhoneNumber { get; set; } = string.Empty;

        [Display(Name = "Phone OTP")]
        [StringLength(6, MinimumLength = 4)]
        public string? PhoneOtp { get; set; }

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
