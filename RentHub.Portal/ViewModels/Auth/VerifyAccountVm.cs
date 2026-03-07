using System.ComponentModel.DataAnnotations;

namespace RentHub.Portal.ViewModels.Auth
{
    public class VerifyAccountVm
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [Display(Name = "OTP Code")]
        [StringLength(6, MinimumLength = 4)]
        public string Otp { get; set; } = string.Empty;
    }
}
