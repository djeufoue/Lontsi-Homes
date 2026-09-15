using Common.CommunicationModels;

namespace RentHub.API.Services.Receipts;

public sealed class ManualPaymentCorrection
{
    public string ActorId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CorrectedAt { get; set; }
    public List<int> ReleasedPeriodIds { get; set; } = new();
    public RentReceiptDto OriginalReceipt { get; set; } = new();
    public RentReceiptDto? ReplacementReceipt { get; set; }
}
