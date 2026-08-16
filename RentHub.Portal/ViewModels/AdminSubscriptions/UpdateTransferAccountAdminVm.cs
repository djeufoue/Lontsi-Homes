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

        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Receiving number must contain only digits and may start with +.")]
        [StringLength(40)]
        public string? PhoneNumber { get; set; }

        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Country code must contain only digits and may start with +.")]
        [StringLength(8)]
        public string? CountryCode { get; set; }

        [StringLength(280)]
        public string? Notes { get; set; }
    }
}
