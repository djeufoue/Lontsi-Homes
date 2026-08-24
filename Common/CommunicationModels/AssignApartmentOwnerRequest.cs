using Common.Enums;
using Common.Helpers;
using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    public class AssignApartmentOwnerRequest
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        public string? FullName { get; set; }

        private string? _countryCode;
        private string? _phoneNumber;

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

        [Required]
        public ApartmentMemberRoleEnum Role { get; set; } = ApartmentMemberRoleEnum.Owner;

        [Required]
        public PermissionLevelEnum Permission { get; set; }
    }
}
