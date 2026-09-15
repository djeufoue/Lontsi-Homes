using System;

namespace Common.CommunicationModels
{
    public sealed class PaymentCorrectionSummaryDto
    {
        public int OriginalPaymentId { get; set; }
        public string OriginalReceiptNumber { get; set; } = string.Empty;
        public int? ReplacementPaymentId { get; set; }
        public string ReplacementReceiptNumber { get; set; } = string.Empty;
        public DateTimeOffset CorrectedAt { get; set; }
        public string Reason { get; set; } = string.Empty;
        public decimal RemovedAmount { get; set; }
        public string Currency { get; set; } = string.Empty;
        public bool EmailsSent { get; set; }
    }
}
