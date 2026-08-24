using System.ComponentModel.DataAnnotations;
using Common.Enums;
using Common.Helpers;

namespace Common.CommunicationModels
{
    public class UpsertSystemTransferAccountRequest
    {
        [Required]
        [EnumDataType(typeof(PayoutChannelEnum))]
        public PayoutChannelEnum Channel { get; set; }

        [Required]
        [StringLength(160)]
        public string AccountName { get; set; } = string.Empty;

        private string? _phoneNumber;
        private string? _countryCode;

        [RegularExpression(PhoneNumberHelper.DigitsWithOptionalLeadingPlusPattern, ErrorMessage = "Phone number must contain only digits and may start with +.")]
        [StringLength(40)]
        public string? PhoneNumber
        {
            get => _phoneNumber;
            set => _phoneNumber = PhoneNumberHelper.Normalize(value);
        }

        [RegularExpression(PhoneNumberHelper.CountryCodePattern, ErrorMessage = "Country code must contain only digits and may start with +.")]
        [StringLength(8)]
        public string? CountryCode
        {
            get => _countryCode;
            set => _countryCode = PhoneNumberHelper.Normalize(value);
        }

        [StringLength(280)]
        public string? Notes { get; set; }
    }
}
