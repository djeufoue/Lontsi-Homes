using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace Common.CommunicationModels
{
    public class StartSubscriptionCheckoutRequest
    {
        [Required]
        [EnumDataType(typeof(PaymentMethodEnum))]
        public PaymentMethodEnum PaymentMethod { get; set; } = PaymentMethodEnum.Momo;

        public bool AllowAutomaticCardPayments { get; set; }

        public string? MobileMoneyPhoneNumber { get; set; }
    }
}
