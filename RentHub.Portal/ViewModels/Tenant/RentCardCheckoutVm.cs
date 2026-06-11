namespace RentHub.Portal.ViewModels.Tenant
{
    public class RentCardCheckoutVm
    {
        public int PaymentId { get; set; }
        public int TenancyId { get; set; }
        public decimal RentAmount { get; set; }
        public string RentCurrency { get; set; } = "XAF";
        public decimal ChargeAmount { get; set; }
        public string ChargeCurrency { get; set; } = "USD";
        public string PaymentReference { get; set; } = string.Empty;
        public string ProviderReference { get; set; } = string.Empty;
        public string PublishableKey { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string ReturnUrl { get; set; } = string.Empty;
        public string PropertyName { get; set; } = string.Empty;
        public string ApartmentName { get; set; } = string.Empty;
        public string PeriodLabel { get; set; } = string.Empty;
    }
}
