using Common.CommunicationModels;
using LontsiHomes.API.Models.Entities;

namespace LontsiHomes.API.Services.Payments
{
    /// <summary>
    /// Handles card payments via a payment gateway (e.g. PayPal or Stripe).  This
    /// implementation is a placeholder and should be replaced with real integration.
    /// </summary>
    public class CardPaymentService : IPaymentService
    {
        public async Task<PaymentResult> ProcessPaymentAsync(Payment payment, PaymentRequest request)
        {
            // TODO: Integrate with a card payment provider. Use request.CardNumber, CardExpiry and CardCvv.
            await Task.Delay(100);
            return new PaymentResult
            {
                Success = true,
                TransactionId = payment.TransactionId,
                Status = "SUCCESS",
                ProviderResponse = "Simulated card payment processed."
            };
        }
    }
}