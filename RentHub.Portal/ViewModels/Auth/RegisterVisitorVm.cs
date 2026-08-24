using System.ComponentModel.DataAnnotations;

namespace RentHub.Portal.ViewModels.Auth
{
    public class RegisterVisitorVm
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Display(Name = "Full name (optional)")]
        public string? FullName { get; set; }

        private string? _phoneNumber;
        private string? _whatsAppPhoneNumber;

        [Display(Name = "Phone number (optional)")]
        [RegularExpression(Common.Helpers.PhoneNumberHelper.DigitsWithOptionalLeadingPlusPattern, ErrorMessage = "Phone number must contain only digits and may start with +.")]
        public string? PhoneNumber
        {
            get => _phoneNumber;
            set => _phoneNumber = Common.Helpers.PhoneNumberHelper.Normalize(value);
        }

        [Display(Name = "WhatsApp number (optional)")]
        [RegularExpression(Common.Helpers.PhoneNumberHelper.DigitsWithOptionalLeadingPlusPattern, ErrorMessage = "WhatsApp number must contain only digits and may start with +.")]
        public string? WhatsAppPhoneNumber
        {
            get => _whatsAppPhoneNumber;
            set => _whatsAppPhoneNumber = Common.Helpers.PhoneNumberHelper.Normalize(value);
        }

        [Required]
        public string Password { get; set; } = string.Empty;

        [Required]
        [Compare(nameof(Password), ErrorMessage = "Password and Confirm Password must match.")]
        [Display(Name = "Confirm Password")]
        public string ConfirmPassword { get; set; } = string.Empty;

        public string? ReturnUrl { get; set; }
    }
}
