using System.ComponentModel.DataAnnotations;
using Common.Enums;
using Common.Helpers;

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

        private string? _countryCode;
        private string? _phoneNumber;
        private string? _payoutPhoneNumber;
        private string? _whatsAppPhoneNumber;

        [RegularExpression(PhoneNumberHelper.CountryCodePattern, ErrorMessage = "Country code must contain only digits and may start with +.")]
        public string? CountryCode
        {
            get => _countryCode;
            set => _countryCode = PhoneNumberHelper.Normalize(value);
        }

        [Display(Name = "Phone number")]
        [RegularExpression(PhoneNumberHelper.DigitsWithOptionalLeadingPlusPattern, ErrorMessage = "Phone number must contain only digits and may start with +.")]
        public string? PhoneNumber
        {
            get => _phoneNumber;
            set => _phoneNumber = PhoneNumberHelper.Normalize(value);
        }

        [Display(Name = "Payout number")]
        [RegularExpression(PhoneNumberHelper.DigitsWithOptionalLeadingPlusPattern, ErrorMessage = "Payout number must contain only digits and may start with +.")]
        public string? PayoutPhoneNumber
        {
            get => _payoutPhoneNumber;
            set => _payoutPhoneNumber = PhoneNumberHelper.Normalize(value);
        }

        [Display(Name = "Payout channel")]
        public PayoutChannelEnum? PayoutChannel { get; set; }

        [Display(Name = "WhatsApp number")]
        [RegularExpression(PhoneNumberHelper.DigitsWithOptionalLeadingPlusPattern, ErrorMessage = "WhatsApp number must contain only digits and may start with +.")]
        public string? WhatsAppPhoneNumber
        {
            get => _whatsAppPhoneNumber;
            set => _whatsAppPhoneNumber = PhoneNumberHelper.Normalize(value);
        }

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
