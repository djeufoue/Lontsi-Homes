using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RentHub.API.Models.Entities
{
    /// <summary>
    /// A property groups multiple apartments. Owned by a landlord.
    /// </summary>
    public class Property
    {
        [Key]
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string? CountryCode { get; set; }
        public string? CountryIsoCode { get; set; }
        public string? Description { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public bool MapEnabled { get; set; } = false;
        public bool SuccessDialogShowSuccessMessages { get; set; } = true;
        public bool SuccessDialogAutoCloseEnabled { get; set; }
        public int SuccessDialogAutoCloseSeconds { get; set; } = 5;
        public string SuccessDialogPosition { get; set; } = "bottom-center";
        public bool AutomaticPaymentsEnabled { get; set; }

        // Owner of the property
        public string LandlordId { get; set; } = string.Empty;
        public ApplicationUser? Landlord { get; set; }

        // Collection of apartments within this property
        public ICollection<Apartment> Apartments { get; set; } = new List<Apartment>();

        // Audit fields
        public bool IsDeleted { get; set; } = false;
        public string? CreatedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string? UpdatedBy { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public string? DeletedBy { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
    }
}
