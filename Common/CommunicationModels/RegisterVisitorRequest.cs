using System.ComponentModel.DataAnnotations;
using Common.Helpers;

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

        private string? _phoneNumber;
        private string? _whatsAppPhoneNumber;

        [Display(Name = "Phone number")]
        [RegularExpression(PhoneNumberHelper.DigitsWithOptionalLeadingPlusPattern, ErrorMessage = "Phone number must contain only digits and may start with +.")]
        public string? PhoneNumber
        {
            get => _phoneNumber;
            set => _phoneNumber = PhoneNumberHelper.Normalize(value);
        }

        [Display(Name = "WhatsApp number")]
        [RegularExpression(PhoneNumberHelper.DigitsWithOptionalLeadingPlusPattern, ErrorMessage = "WhatsApp number must contain only digits and may start with +.")]
        public string? WhatsAppPhoneNumber
        {
            get => _whatsAppPhoneNumber;
            set => _whatsAppPhoneNumber = PhoneNumberHelper.Normalize(value);
        }
    }
}
