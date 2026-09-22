using System.ComponentModel.DataAnnotations;

namespace LontsiHomes.Portal.ViewModels.Auth;

public class ForgotPasswordVm
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;
}
