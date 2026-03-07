using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    /// <summary>
    /// DTO used when an administrator creates a new subscription plan.  All fields are required
    /// except for the optional limits on properties and apartments per property.
    /// </summary>
    public class CreateSubscriptionPlanRequest
    {
        [Required]
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        [Range(0, double.MaxValue)]
        public decimal Price { get; set; }
        [Range(1, int.MaxValue)]
        public int DurationInDays { get; set; }
        public int? MaxProperties { get; set; }
        public int? MaxApartmentsPerProperty { get; set; }
    }
}