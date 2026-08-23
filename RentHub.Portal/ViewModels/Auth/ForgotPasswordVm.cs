using System.ComponentModel.DataAnnotations;

namespace RentHub.Portal.ViewModels.Auth;

public class ForgotPasswordVm
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;
}
