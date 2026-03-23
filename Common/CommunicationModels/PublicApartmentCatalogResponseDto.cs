using System.Collections.Generic;

namespace Common.CommunicationModels
{
    public class PublicApartmentCatalogResponseDto
    {
        public List<PublicApartmentCatalogItemDto> Items { get; set; } = new();
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
    }

    public class PublicApartmentCatalogItemDto
    {
        public int ApartmentId { get; set; }
        public int PropertyId { get; set; }
        public string ApartmentName { get; set; } = string.Empty;
        public string PropertyName { get; set; } = string.Empty;
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
        public string? LeadImageUrl { get; set; }
    }
}
