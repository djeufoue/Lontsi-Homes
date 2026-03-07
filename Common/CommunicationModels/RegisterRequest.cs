using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    /// <summary>
    /// Request payload for user registration.  Self-registration always creates a landlord
    /// account.  Other account types (Owner, Manager, Tenant) are created by landlords
    /// through dedicated endpoints.  The PlanId is required to choose a subscription.
    /// </summary>
    public class RegisterRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;

        public string FullName { get; set; } = string.Empty;
        public string CountryCode { get; set; } = string.Empty;

        /// <summary>
        /// Optional account type input.  This field is ignored for self-registration and is kept
        /// for backward compatibility.  All self-registered users become landlords.
        /// </summary>
        public string? AccountType { get; set; }

        /// <summary>
        /// The subscription plan identifier to associate with the landlord.  This is required
        /// during self-registration.
        /// </summary>
        [Required]
        public int? PlanId { get; set; }
    }
}