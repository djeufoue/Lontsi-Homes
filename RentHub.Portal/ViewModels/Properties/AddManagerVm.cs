using Common.Enums;
using System.ComponentModel.DataAnnotations;

namespace RentHub.Portal.ViewModels.Properties
{
    public class AddManagerVm
    {
        [Required, EmailAddress] public string Email { get; set; } = "";
        [Required] public string FullName { get; set; } = "";
        public string? CountryCode { get; set; }
        public PermissionLevelEnum Permission { get; set; } = PermissionLevelEnum.ReadOnly;
    }
}
