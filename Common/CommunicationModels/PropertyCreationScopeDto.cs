namespace Common.CommunicationModels
{
    public class PropertyCreationScopeDto
    {
        public string LandlordId { get; set; } = string.Empty;
        public string LandlordName { get; set; } = string.Empty;

        public int CurrentProperties { get; set; }
        public int? MaxProperties { get; set; }

        public bool SubscriptionApproved { get; set; }
        public bool KycApproved { get; set; }
        public bool PlatformTermsAccepted { get; set; }
        public bool StripePayoutSetupComplete { get; set; }
        public bool StripePayoutSetupRequired { get; set; } = true;
        public bool CanCreate { get; set; }

        public string StatusMessage { get; set; } = string.Empty;
    }
}
