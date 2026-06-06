namespace RentHub.Portal.ViewModels.Profile
{
    public class SubscriptionCardCheckoutVm
    {
        public int PlanId { get; set; }
        public string PlanName { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public string PaymentReference { get; set; } = string.Empty;
        public string ProviderReference { get; set; } = string.Empty;
        public string PublishableKey { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string ReturnUrl { get; set; } = string.Empty;
    }
}
