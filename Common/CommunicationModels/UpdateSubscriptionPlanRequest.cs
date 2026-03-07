using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    /// <summary>
    /// DTO used when updating an existing subscription plan.  All fields mirror the
    /// creation DTO and are optional; null values indicate the field should not be
    /// modified.
    /// </summary>
    public class UpdateSubscriptionPlanRequest
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        [Range(0, double.MaxValue)]
        public decimal? Price { get; set; }
        [Range(1, int.MaxValue)]
        public int? DurationInDays { get; set; }
        public int? MaxProperties { get; set; }
        public int? MaxApartmentsPerProperty { get; set; }
    }
}