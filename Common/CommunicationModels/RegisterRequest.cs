using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace Common.CommunicationModels
{
    /// <summary>
    /// Request payload for user registration. Self-registration always creates a landlord
    /// account. Other account types (Owner, Manager, Tenant) are created by landlords
    /// through dedicated endpoints. A plan can be chosen during registration or later
    /// from the subscription flow.
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

        [Required]
        [Display(Name = "Payout number")]
        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Payout number must contain only digits and may start with +.")]
        public string PayoutPhoneNumber { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Payout channel")]
        public PayoutChannelEnum? PayoutChannel { get; set; }

        [Required]
        [Display(Name = "WhatsApp number")]
        [RegularExpression(@"^\+?\d+$", ErrorMessage = "WhatsApp number must contain only digits and may start with +.")]
        public string WhatsAppPhoneNumber { get; set; } = string.Empty;

        /// <summary>
        /// Optional account type input. This field is ignored for self-registration and is kept
        /// for backward compatibility. All self-registered users become landlords.
        /// </summary>
        public string? AccountType { get; set; }

        /// <summary>
        /// The optional subscription plan identifier to associate with the landlord.
        /// </summary>
        public int? PlanId { get; set; }
    }
}
