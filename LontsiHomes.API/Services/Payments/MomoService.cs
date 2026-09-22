using Common.CommunicationModels;
using LontsiHomes.API.Models.Entities;

namespace LontsiHomes.API.Services.Payments
{
    /// <summary>
    /// Handles MTN Mobile Money transactions.  This is a stub implementation; integrate
    /// with the actual MTN MoMo API by following their documentation and adding
    /// authentication, transfer and status endpoints similar to OrangeMoneyService.
    /// </summary>
    public class MomoService : IPaymentService
    {
        public async Task<PaymentResult> ProcessPaymentAsync(Payment payment, PaymentRequest request)
        {
            // TODO: Integrate with MTN MoMo API.  For now we simulate a successful payment.
            await Task.Delay(100); // Simulate network latency
            return new PaymentResult
            {
                Success = true,
                TransactionId = payment.TransactionId,
                Status = "SUCCESS",
                ProviderResponse = "Simulated MTN MoMo payment processed."
            };
        }
    }
}