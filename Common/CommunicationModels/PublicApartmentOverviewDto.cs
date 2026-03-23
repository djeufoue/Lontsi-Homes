using System.Collections.Generic;

namespace Common.CommunicationModels
{
    public class PublicApartmentOverviewDto
    {
        public int ApartmentId { get; set; }
        public int PropertyId { get; set; }
        public string ApartmentName { get; set; } = string.Empty;
        public string PropertyName { get; set; } = string.Empty;
        public string PropertyDescription { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public decimal DepositPrice { get; set; }
        public int Area { get; set; }
        public int NumberOfRooms { get; set; }
        public int NumberOfBathrooms { get; set; }
        public int? FloorNumber { get; set; }
        public string LandlordName { get; set; } = string.Empty;
        public List<PublicMediaItemDto> PropertyImages { get; set; } = new();
        public List<PublicMediaItemDto> ApartmentImages { get; set; } = new();
    }

    public class PublicMediaItemDto
    {
        public int DocumentId { get; set; }
        public string Url { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
    }
}
