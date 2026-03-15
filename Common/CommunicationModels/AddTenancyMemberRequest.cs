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

        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Country code must contain only digits and may start with +.")]
        public string? CountryCode { get; set; }

        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Phone number must contain only digits and may start with +.")]
        public string? PhoneNumber { get; set; }

        [Required]
        public TenancyMemberRoleEnum Role { get; set; }
    }
}
