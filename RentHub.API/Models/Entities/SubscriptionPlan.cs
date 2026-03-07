using System.ComponentModel.DataAnnotations;

namespace RentHub.API.Models.Entities
{
    /// <summary>
    /// Represents a pricing tier that landlords can subscribe to.  Duration is expressed
    /// in days to allow monthly, quarterly or annual plans.
    /// </summary>
    public class SubscriptionPlan
    {
        [Key]
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public decimal Price { get; set; }
        public int DurationInDays { get; set; }
        // Maximum number of properties a landlord can advertise under this plan (optional)
        public int? MaxProperties { get; set; }
        // Maximum number of apartments per property that a landlord can add under this plan
        public int? MaxApartmentsPerProperty { get; set; }

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