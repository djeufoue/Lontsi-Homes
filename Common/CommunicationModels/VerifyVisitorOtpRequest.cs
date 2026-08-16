using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    public class VerifyVisitorOtpRequest
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

        [Display(Name = "WhatsApp OTP")]
        [StringLength(6, MinimumLength = 4)]
        public string? WhatsAppOtp { get; set; }
    }
}
