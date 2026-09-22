using System.ComponentModel.DataAnnotations;

namespace LontsiHomes.Portal.ViewModels.Auth
{
    public class LoginVm
    {
        [Required, EmailAddress]
        public string Email { get; set; } = "";

        [Required]
        public string Password { get; set; } = "";

        public string? ReturnUrl { get; set; }
    }
}
