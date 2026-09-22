using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace LontsiHomes.Portal.ViewModels.Auth
{
    public class RegisterVm
    {
        [Required, EmailAddress]
        public string Email { get; set; } = "";

        [Required]
        [Display(Name = "First name")]
        public string FirstName { get; set; } = "";

        [Required]
        [Display(Name = "Last name")]
        public string LastName { get; set; } = "";

        private string? _countryCode;
        private string? _phoneNumber;
        private string _payoutPhoneNumber = string.Empty;
        private string _whatsAppPhoneNumber = string.Empty;

        [Display(Name = "Country code")]
        [RegularExpression(Common.Helpers.PhoneNumberHelper.CountryCodePattern, ErrorMessage = "Country code must contain only digits and may start with +.")]
        public string? CountryCode
        {
            get => _countryCode;
            set => _countryCode = Common.Helpers.PhoneNumberHelper.Normalize(value);
        }

        [Display(Name = "Phone number (optional)")]
        [RegularExpression(Common.Helpers.PhoneNumberHelper.DigitsWithOptionalLeadingPlusPattern, ErrorMessage = "Phone number must contain only digits and may start with +.")]
        public string? PhoneNumber
        {
            get => _phoneNumber;
            set => _phoneNumber = Common.Helpers.PhoneNumberHelper.Normalize(value);
        }

        [Display(Name = "Rent payout number")]
        [RegularExpression(Common.Helpers.PhoneNumberHelper.DigitsWithOptionalLeadingPlusPattern, ErrorMessage = "Payout number must contain only digits and may start with +.")]
        public string PayoutPhoneNumber
        {
            get => _payoutPhoneNumber;
            set => _payoutPhoneNumber = Common.Helpers.PhoneNumberHelper.NormalizeOrEmpty(value);
        }

        [Display(Name = "Payout channel")]
        public PayoutChannelEnum? PayoutChannel { get; set; }

        [Display(Name = "WhatsApp number")]
        [RegularExpression(Common.Helpers.PhoneNumberHelper.DigitsWithOptionalLeadingPlusPattern, ErrorMessage = "WhatsApp number must contain only digits and may start with +.")]
        public string WhatsAppPhoneNumber
        {
            get => _whatsAppPhoneNumber;
            set => _whatsAppPhoneNumber = Common.Helpers.PhoneNumberHelper.NormalizeOrEmpty(value);
        }

        [Required]
        public string Password { get; set; } = "";

        [Required]
        [Compare(nameof(Password), ErrorMessage = "Password and Confirm Password must match.")]
        [Display(Name = "Confirm Password")]
        public string ConfirmPassword { get; set; } = "";

        public int? PlanId { get; set; }
        public string? ReturnUrl { get; set; }
    }
}
