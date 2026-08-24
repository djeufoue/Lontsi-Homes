using System.Collections.Generic;

namespace Common.CommunicationModels
{
    /// <summary>
    /// Detailed representation of a property including apartments.
    /// </summary>
    public class PropertyDetailDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string? CountryCode { get; set; }
        public string? CountryIsoCode { get; set; }
        public string? Description { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public bool MapEnabled { get; set; }
        public bool SuccessDialogShowSuccessMessages { get; set; } = true;
        public bool SuccessDialogAutoCloseEnabled { get; set; }
        public int SuccessDialogAutoCloseSeconds { get; set; } = 5;
        public string SuccessDialogPosition { get; set; } = "bottom-center";

        public string LandlordId { get; set; } = string.Empty;
        public string LandlordName { get; set; } = string.Empty;
        public string LandlordEmail { get; set; } = string.Empty;

        public ICollection<ApartmentDto> Apartments { get; set; } = new List<ApartmentDto>();
    }
}
