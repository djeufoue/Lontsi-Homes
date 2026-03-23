using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace Common.CommunicationModels
{
    public class StartSubscriptionCheckoutRequest
    {
        [Required]
        public PaymentMethodEnum PaymentMethod { get; set; }

        public bool AllowAutomaticCardPayments { get; set; }
    }
}
