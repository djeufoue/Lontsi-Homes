using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace Common.CommunicationModels
{
    public class AddTenancyMemberRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        public string? FullName { get; set; }
        public string? CountryCode { get; set; }

        [Required]
        public TenancyMemberRoleEnum Role { get; set; }
    }
}
