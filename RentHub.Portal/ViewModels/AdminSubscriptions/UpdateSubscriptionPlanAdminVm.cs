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

        [Range(1, int.MaxValue)]
        public int DurationInDays { get; set; }

        public bool UnlimitedProperties { get; set; }
        public int? MaxProperties { get; set; }

        public bool UnlimitedApartmentsPerProperty { get; set; }
        public int? MaxApartmentsPerProperty { get; set; }
    }
}
