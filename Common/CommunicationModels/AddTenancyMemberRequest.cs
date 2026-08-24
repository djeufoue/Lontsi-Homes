using System.ComponentModel.DataAnnotations;
using Common.Enums;
using Common.Helpers;

namespace Common.CommunicationModels
{
    public class AddTenancyMemberRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        public string? FullName { get; set; }

        private string? _countryCode;
        private string? _phoneNumber;
        private string? _whatsAppPhoneNumber;

        [RegularExpression(PhoneNumberHelper.CountryCodePattern, ErrorMessage = "Country code must contain only digits and may start with +.")]
        public string? CountryCode
        {
            get => _countryCode;
            set => _countryCode = PhoneNumberHelper.Normalize(value);
        }

        [RegularExpression(PhoneNumberHelper.DigitsWithOptionalLeadingPlusPattern, ErrorMessage = "Phone number must contain only digits and may start with +.")]
        public string? PhoneNumber
        {
            get => _phoneNumber;
            set => _phoneNumber = PhoneNumberHelper.Normalize(value);
        }

        [RegularExpression(PhoneNumberHelper.DigitsWithOptionalLeadingPlusPattern, ErrorMessage = "WhatsApp number must contain only digits and may start with +.")]
        public string? WhatsAppPhoneNumber
        {
            get => _whatsAppPhoneNumber;
            set => _whatsAppPhoneNumber = PhoneNumberHelper.Normalize(value);
        }

        [Required]
        public TenancyMemberRoleEnum Role { get; set; }
    }
}
