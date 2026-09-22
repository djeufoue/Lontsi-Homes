using System.ComponentModel.DataAnnotations;

namespace LontsiHomes.Portal.ViewModels.Auth
{
    public class VerifyAccountVm
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Email OTP")]
        [StringLength(6, MinimumLength = 4)]
        public string EmailOtp { get; set; } = string.Empty;

        [Display(Name = "Phone OTP")]
        [StringLength(6, MinimumLength = 4)]
        public string? PhoneOtp { get; set; }

        [Display(Name = "Subscription payment OTP")]
        [StringLength(6, MinimumLength = 4)]
        public string? SubscriptionPaymentOtp { get; set; }

        [Display(Name = "Payout OTP")]
        [StringLength(6, MinimumLength = 4)]
        public string? PayoutOtp { get; set; }

        [Display(Name = "WhatsApp OTP")]
        [StringLength(6, MinimumLength = 4)]
        public string? WhatsAppOtp { get; set; }

        public string? ReturnUrl { get; set; }
    }
}
