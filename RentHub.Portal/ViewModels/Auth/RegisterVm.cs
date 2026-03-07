using System.ComponentModel.DataAnnotations;

namespace RentHub.Portal.ViewModels.Auth
{
    public class RegisterVm
    {
        [Required, EmailAddress]
        public string Email { get; set; } = "";

        [Required]
        public string FullName { get; set; } = "";

        public string? CountryCode { get; set; }

        [Required]
        public string Password { get; set; } = "";

        [Required]
        public int PlanId { get; set; }
    }
}
