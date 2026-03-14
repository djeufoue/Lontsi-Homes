using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    /// <summary>
    /// Request payload for user registration. Self-registration always creates a landlord
    /// account. Other account types (Owner, Manager, Tenant) are created by landlords
    /// through dedicated endpoints. The PlanId is required to choose a subscription.
    /// </summary>
    public class RegisterRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;

        [Required]
        [Display(Name = "First name")]
        public string FirstName { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Last name")]
        public string LastName { get; set; } = string.Empty;

        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Country code must contain only digits and may start with +.")]
        public string? CountryCode { get; set; }

        [Display(Name = "Phone number")]
        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Phone number must contain only digits and may start with +.")]
        public string? PhoneNumber { get; set; }

        /// <summary>
        /// Optional account type input. This field is ignored for self-registration and is kept
        /// for backward compatibility. All self-registered users become landlords.
        /// </summary>
        public string? AccountType { get; set; }

        /// <summary>
        /// The subscription plan identifier to associate with the landlord. This is required
        /// during self-registration.
        /// </summary>
        [Required]
        public int? PlanId { get; set; }
    }
}
