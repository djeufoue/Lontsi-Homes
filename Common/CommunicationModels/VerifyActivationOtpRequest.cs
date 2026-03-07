using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    public class VerifyActivationOtpRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [StringLength(6, MinimumLength = 4)]
        public string Otp { get; set; } = string.Empty;
    }
}
