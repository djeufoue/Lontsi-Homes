using Common.Enums;
using System.ComponentModel.DataAnnotations;

namespace RentHub.Portal.ViewModels.Properties
{
    public class AddManagerVm
    {
        [Required, EmailAddress] public string Email { get; set; } = "";
        [Required] public string FullName { get; set; } = "";
        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Country code must contain only digits and may start with +.")]
        public string? CountryCode { get; set; }
        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Phone number must contain only digits and may start with +.")]
        public string? PhoneNumber { get; set; }
        public PermissionLevelEnum Permission { get; set; } = PermissionLevelEnum.ReadOnly;
    }
}
