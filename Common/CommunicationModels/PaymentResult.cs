namespace Common.CommunicationModels
{
    /// <summary>
    /// Encapsulates the outcome of a payment transaction.  Used by services and
    /// controllers to return consistent information to callers.
    /// </summary>
    public class PaymentResult
    {
        public bool Success { get; set; }
        public string TransactionId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? ProviderResponse { get; set; }
        public string? ProviderReceiptUrl { get; set; }
    }
}
