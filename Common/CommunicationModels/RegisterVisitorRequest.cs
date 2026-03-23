using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    public class RegisterVisitorRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Full name")]
        public string? FullName { get; set; }

        [Display(Name = "Phone number")]
        [Required]
        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Phone number must contain only digits and may start with +.")]
        public string PhoneNumber { get; set; } = string.Empty;

        [Required]
        [Display(Name = "WhatsApp number")]
        [RegularExpression(@"^\+?\d+$", ErrorMessage = "WhatsApp number must contain only digits and may start with +.")]
        public string WhatsAppPhoneNumber { get; set; } = string.Empty;
    }
}
