using System.ComponentModel.DataAnnotations;
using Common.Helpers;

namespace Common.CommunicationModels
{
    /// <summary>
    /// DTO used when creating a property.
    /// </summary>
    public class CreatePropertyRequest
    {
        [Required]
        public string Name { get; set; } = string.Empty;

        [Required]
        public string City { get; set; } = string.Empty;

        [Required]
        public string Address { get; set; } = string.Empty;

        private string? _countryCode;

        [RegularExpression(PhoneNumberHelper.CountryCodePattern, ErrorMessage = "Country code must contain only digits and may start with +.")]
        public string? CountryCode
        {
            get => _countryCode;
            set => _countryCode = PhoneNumberHelper.Normalize(value);
        }

        [RegularExpression(@"^[A-Za-z]{2}$", ErrorMessage = "Country must be a valid 2-letter ISO code.")]
        public string? CountryIsoCode { get; set; }

        public string? Description { get; set; }

        [Range(-90, 90)]
        public double? Latitude { get; set; }

        [Range(-180, 180)]
        public double? Longitude { get; set; }

        // Optional. Used by Admin/Manager scenarios where creation targets a landlord account.
        public string? LandlordId { get; set; }
    }
}
