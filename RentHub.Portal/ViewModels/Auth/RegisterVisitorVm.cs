using System.ComponentModel.DataAnnotations;

namespace RentHub.Portal.ViewModels.Auth
{
    public class RegisterVisitorVm
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Display(Name = "Full name (optional)")]
        public string? FullName { get; set; }

        [Display(Name = "Phone number (optional)")]
        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Phone number must contain only digits and may start with +.")]
        public string? PhoneNumber { get; set; }

        [Display(Name = "WhatsApp number (optional)")]
        [RegularExpression(@"^\+?\d+$", ErrorMessage = "WhatsApp number must contain only digits and may start with +.")]
        public string? WhatsAppPhoneNumber { get; set; }

        [Required]
        public string Password { get; set; } = string.Empty;

        [Required]
        [Compare(nameof(Password), ErrorMessage = "Password and Confirm Password must match.")]
        [Display(Name = "Confirm Password")]
        public string ConfirmPassword { get; set; } = string.Empty;

        public string? ReturnUrl { get; set; }
    }
}
