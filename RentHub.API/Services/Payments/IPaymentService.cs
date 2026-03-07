using Common.CommunicationModels;
using RentHub.API.Models.Entities;

namespace RentHub.API.Services.Payments
{
    /// <summary>
    /// A generic payment service capable of processing transactions.  Concrete implementations
    /// integrate with specific providers (Orange Money, MTN MoMo, card gateways).
    /// </summary>
    public interface IPaymentService
    {
        Task<PaymentResult> ProcessPaymentAsync(Payment payment, PaymentRequest request);
    }
}