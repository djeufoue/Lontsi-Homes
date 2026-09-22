using Common.CommunicationModels;

namespace LontsiHomes.API.Services.Receipts
{
    public interface IRentReceiptService
    {
        Task<RentReceiptDto?> EnsureReceiptAsync(int paymentId, string actorId, CancellationToken cancellationToken = default);
        Task<RentReceiptDto?> GetReceiptAsync(int paymentId, CancellationToken cancellationToken = default);
        Task<RentReceiptVerificationDto?> VerifyReceiptAsync(string verificationCode, CancellationToken cancellationToken = default);
        Task SendReceiptNotificationsAsync(RentReceiptDto receipt, bool notifyTenant, bool notifyLandlord, CancellationToken cancellationToken = default);
    }
}
