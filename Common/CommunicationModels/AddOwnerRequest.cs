using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace Common.CommunicationModels
{
    /// <summary>
    /// Request payload for assigning an owner to an apartment.  The owner can be an existing user
    /// or a new user created on the fly.  Permission controls whether the owner can create
    /// tenancies or just view information.
    /// </summary>
    public class AddOwnerRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        public string FullName { get; set; } = string.Empty;

        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Country code must contain only digits and may start with +.")]
        public string? CountryCode { get; set; }

        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Phone number must contain only digits and may start with +.")]
        public string? PhoneNumber { get; set; }

        public PermissionLevelEnum Permission { get; set; } = PermissionLevelEnum.ReadOnly;
    }
}
