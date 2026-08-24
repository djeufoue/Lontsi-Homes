namespace Common.CommunicationModels
{
    /// <summary>
    /// Data transfer object representing an apartment for listing and detail views.
    /// Contains basic apartment attributes and identifiers for related property and landlord.
    /// </summary>
    public class ApartmentDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public double Area { get; set; }
        public int FloorNumber { get; set; }
        public string PropertyName { get; set; } = string.Empty;
        public string LandlordName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }
}
