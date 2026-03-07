namespace Common.CommunicationModels
{
    /// <summary>
    /// Lightweight representation of a property used for listings.
    /// Provides summary information without exposing navigation properties.
    /// </summary>
    public class PropertyDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public int ApartmentCount { get; set; }
    }
}