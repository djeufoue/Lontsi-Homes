using System.ComponentModel.DataAnnotations;
using Common.Enums;

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

        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Phone number must contain only digits and may start with +.")]
        [StringLength(40)]
        public string? PhoneNumber { get; set; }

        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Country code must contain only digits and may start with +.")]
        [StringLength(8)]
        public string? CountryCode { get; set; }

        [StringLength(280)]
        public string? Notes { get; set; }
    }
}
