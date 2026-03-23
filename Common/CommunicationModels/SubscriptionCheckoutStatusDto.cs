namespace Common.CommunicationModels
{
    public class SubscriptionCheckoutStatusDto
    {
        public int SubscriptionId { get; set; }
        public int PlanId { get; set; }
        public string PlanName { get; set; } = string.Empty;
        public string PaymentReference { get; set; } = string.Empty;
        public string PaymentStatus { get; set; } = string.Empty;
        public bool SubscriptionApproved { get; set; }
        public bool PaymentCompleted { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
