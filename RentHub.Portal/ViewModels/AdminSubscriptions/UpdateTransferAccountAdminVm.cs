using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace RentHub.Portal.ViewModels.AdminSubscriptions
{
    public class UpdateTransferAccountAdminVm
    {
        [Required]
        [EnumDataType(typeof(PayoutChannelEnum))]
        public PayoutChannelEnum Channel { get; set; }

        [Required]
        [StringLength(160)]
        public string AccountName { get; set; } = string.Empty;

        private string? _phoneNumber;
        private string? _countryCode;

        [RegularExpression(Common.Helpers.PhoneNumberHelper.DigitsWithOptionalLeadingPlusPattern, ErrorMessage = "Receiving number must contain only digits and may start with +.")]
        [StringLength(40)]
        public string? PhoneNumber
        {
            get => _phoneNumber;
            set => _phoneNumber = Common.Helpers.PhoneNumberHelper.Normalize(value);
        }

        [RegularExpression(Common.Helpers.PhoneNumberHelper.CountryCodePattern, ErrorMessage = "Country code must contain only digits and may start with +.")]
        [StringLength(8)]
        public string? CountryCode
        {
            get => _countryCode;
            set => _countryCode = Common.Helpers.PhoneNumberHelper.Normalize(value);
        }

        [StringLength(280)]
        public string? Notes { get; set; }
    }
}
