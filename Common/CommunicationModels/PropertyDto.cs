namespace Common.CommunicationModels
{
    /// <summary>
    /// Lightweight representation of a property used for listings.
    /// </summary>
    public class PropertyDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string? CountryCode { get; set; }
        public string? CountryIsoCode { get; set; }
        public int ApartmentCount { get; set; }

        public string LandlordId { get; set; } = string.Empty;
        public string LandlordName { get; set; } = string.Empty;

        // True if the current authenticated user can change property data.
        public bool CanWrite { get; set; }

        // Owned, Managed, Owner, Tenant, Admin
        public string AccessSource { get; set; } = string.Empty;
        public bool AutomaticPaymentsEnabled { get; set; }
    }
}
