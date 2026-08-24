using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    public class UpdateMemberEmailRequest
    {
        [Required, EmailAddress, MaxLength(256)]
        public string Email { get; set; } = string.Empty;
    }
}
