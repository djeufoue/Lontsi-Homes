using Common.Enums;
using System.ComponentModel.DataAnnotations;

namespace LontsiHomes.Portal.ViewModels.Properties
{
    public class AddManagerVm
    {
        [Required, EmailAddress] public string Email { get; set; } = "";
        [Required] public string FullName { get; set; } = "";
        private string? _countryCode;
        private string? _phoneNumber;
        private string? _whatsAppPhoneNumber;

        [RegularExpression(Common.Helpers.PhoneNumberHelper.CountryCodePattern, ErrorMessage = "Country code must contain only digits and may start with +.")]
        public string? CountryCode
        {
            get => _countryCode;
            set => _countryCode = Common.Helpers.PhoneNumberHelper.Normalize(value);
        }

        [RegularExpression(Common.Helpers.PhoneNumberHelper.DigitsWithOptionalLeadingPlusPattern, ErrorMessage = "Phone number must contain only digits and may start with +.")]
        public string? PhoneNumber
        {
            get => _phoneNumber;
            set => _phoneNumber = Common.Helpers.PhoneNumberHelper.Normalize(value);
        }

        [RegularExpression(Common.Helpers.PhoneNumberHelper.DigitsWithOptionalLeadingPlusPattern, ErrorMessage = "WhatsApp number must contain only digits and may start with +.")]
        public string? WhatsAppPhoneNumber
        {
            get => _whatsAppPhoneNumber;
            set => _whatsAppPhoneNumber = Common.Helpers.PhoneNumberHelper.Normalize(value);
        }

        public PermissionLevelEnum Permission { get; set; } = PermissionLevelEnum.ReadOnly;
    }
}
