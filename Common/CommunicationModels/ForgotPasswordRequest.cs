using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    public class ForgotPasswordRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;
    }
}