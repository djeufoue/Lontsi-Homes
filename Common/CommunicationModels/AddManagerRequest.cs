using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace Common.CommunicationModels
{
    /// <summary>
    /// Request payload for assigning a manager to a property.  The manager can be an existing user
    /// or a new user created on the fly.  Permission determines whether the manager can
    /// modify property data or only view it.
    /// </summary>
    public class AddManagerRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        public string FullName { get; set; } = string.Empty;

        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Country code must contain only digits and may start with +.")]
        public string CountryCode { get; set; } = string.Empty;

        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Phone number must contain only digits and may start with +.")]
        public string PhoneNumber { get; set; } = string.Empty;

        public PermissionLevelEnum Permission { get; set; } = PermissionLevelEnum.ReadOnly;
    }
}
