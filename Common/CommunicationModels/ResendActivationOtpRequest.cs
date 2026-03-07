using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    public class ResendActivationOtpRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;
    }
}
