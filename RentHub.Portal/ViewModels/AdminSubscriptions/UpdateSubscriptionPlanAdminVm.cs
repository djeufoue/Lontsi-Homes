using System.ComponentModel.DataAnnotations;

namespace RentHub.Portal.ViewModels.AdminSubscriptions
{
    public class UpdateSubscriptionPlanAdminVm
    {
        [Required]
        public int PlanId { get; set; }

        [Required]
        public string Name { get; set; } = string.Empty;

        public string? Description { get; set; }

        [Range(0, double.MaxValue)]
        public decimal Price { get; set; }

        [Range(0, double.MaxValue)]
        public decimal? AnnualPrice { get; set; }

        [Range(1, int.MaxValue)]
        public int DurationInDays { get; set; }

        public bool UnlimitedProperties { get; set; }
        public int? MaxProperties { get; set; }

        public bool UnlimitedApartmentsPerProperty { get; set; }
        public int? MaxApartmentsPerProperty { get; set; }

        public bool UnlimitedTotalApartments { get; set; }
        public int? MaxTotalApartments { get; set; }

        public string? AudienceLabel { get; set; }
        public string? FeatureHighlights { get; set; }
        public bool IsRecommended { get; set; }
        public bool IsContactSales { get; set; }
        public int DisplayOrder { get; set; }
    }
}
