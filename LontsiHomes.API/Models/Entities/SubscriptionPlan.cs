using System.ComponentModel.DataAnnotations;

namespace LontsiHomes.API.Models.Entities
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
        public decimal? AnnualPrice { get; set; }
        public int DurationInDays { get; set; }
        // Maximum number of properties a landlord can advertise under this plan (optional)
        public int? MaxProperties { get; set; }
        // Maximum number of apartments per property that a landlord can add under this plan
        public int? MaxApartmentsPerProperty { get; set; }
        public int? MaxTotalApartments { get; set; }
        public string? AudienceLabel { get; set; }
        public string? FeatureHighlights { get; set; }
        public bool IsRecommended { get; set; }
        public bool IsContactSales { get; set; }
        public int DisplayOrder { get; set; }

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
