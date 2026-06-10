namespace Common.CommunicationModels
{
    /// <summary>
    /// Data transfer object exposing subscription plan information to clients.
    /// </summary>
    public class SubscriptionPlanDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public decimal? AnnualPrice { get; set; }
        public int DurationInDays { get; set; }
        public string Description { get; set; } = string.Empty;
        public int? MaxProperties { get; set; }
        public int? MaxApartmentsPerProperty { get; set; }
        public int? MaxTotalApartments { get; set; }
        public string AudienceLabel { get; set; } = string.Empty;
        public string FeatureHighlights { get; set; } = string.Empty;
        public bool IsRecommended { get; set; }
        public bool IsContactSales { get; set; }
        public int DisplayOrder { get; set; }
    }
}
