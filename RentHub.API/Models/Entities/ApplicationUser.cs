using Microsoft.AspNetCore.Identity;

namespace RentHub.API.Models.Entities
{
    /// <summary>
    /// Represents an authenticated user in the system.  We use long as the primary key type
    /// to allow for future scalability. Additional profile fields can be added as needed.
    /// </summary>
    public class ApplicationUser : IdentityUser
    {
        public string? FullName { get; set; }
        public string? CountryCode { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        // Navigation properties
        public ICollection<Property> OwnedProperties { get; set; } = new List<Property>();
    }
}