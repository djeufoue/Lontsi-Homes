using Common.Enums;

namespace Common.CommunicationModels
{
    public class SubscriptionCheckoutSessionDto
    {
        public int SubscriptionId { get; set; }
        public int PlanId { get; set; }
        public string PlanName { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "XAF";
        public PaymentMethodEnum PaymentMethod { get; set; }
        public bool AllowAutomaticCardPayments { get; set; }
        public string PaymentReference { get; set; } = string.Empty;
        public string AuthorizationUrl { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }
}
