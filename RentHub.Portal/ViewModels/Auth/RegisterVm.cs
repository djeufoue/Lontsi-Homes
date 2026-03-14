using System.ComponentModel.DataAnnotations;

namespace RentHub.Portal.ViewModels.Auth
{
    public class RegisterVm
    {
        [Required, EmailAddress]
        public string Email { get; set; } = "";

        [Required]
        [Display(Name = "First name")]
        public string FirstName { get; set; } = "";

        [Required]
        [Display(Name = "Last name")]
        public string LastName { get; set; } = "";

        [Display(Name = "Country code")]
        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Country code must contain only digits and may start with +.")]
        public string? CountryCode { get; set; }

        [Display(Name = "Phone number (optional)")]
        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Phone number must contain only digits and may start with +.")]
        public string? PhoneNumber { get; set; }

        [Required]
        public string Password { get; set; } = "";

        [Required]
        [Compare(nameof(Password), ErrorMessage = "Password and Confirm Password must match.")]
        [Display(Name = "Confirm Password")]
        public string ConfirmPassword { get; set; } = "";

        [Required]
        public int PlanId { get; set; }
    }
}
