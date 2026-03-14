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
        public string? Description { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }

        public string LandlordId { get; set; } = string.Empty;
        public string LandlordName { get; set; } = string.Empty;

        public ICollection<ApartmentDto> Apartments { get; set; } = new List<ApartmentDto>();
    }
}
