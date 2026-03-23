using System.ComponentModel.DataAnnotations;

namespace RentHub.Portal.ViewModels.Auth
{
    public class VerifyVisitorAccountVm
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Email OTP")]
        [StringLength(6, MinimumLength = 4)]
        public string EmailOtp { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Phone OTP")]
        [StringLength(6, MinimumLength = 4)]
        public string PhoneOtp { get; set; } = string.Empty;

        [Required]
        [Display(Name = "WhatsApp OTP")]
        [StringLength(6, MinimumLength = 4)]
        public string WhatsAppOtp { get; set; } = string.Empty;

        public string? ReturnUrl { get; set; }
    }
}
