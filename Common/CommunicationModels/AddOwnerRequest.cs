using System.ComponentModel.DataAnnotations;
using Common.Enums;
using Common.Helpers;

namespace Common.CommunicationModels
{
    /// <summary>
    /// Request payload for assigning an owner to an apartment.  The owner can be an existing user
    /// or a new user created on the fly.  Permission controls whether the owner can create
    /// tenancies or just view information.
    /// </summary>
    public class AddOwnerRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        public string FullName { get; set; } = string.Empty;

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

        public PermissionLevelEnum Permission { get; set; } = PermissionLevelEnum.ReadOnly;
    }
}
