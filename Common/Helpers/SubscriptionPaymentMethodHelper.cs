using Common.Enums;

namespace Common.Helpers
{
    public static class SubscriptionPaymentMethodHelper
    {
        public static bool IsSupported(PaymentMethodEnum paymentMethod)
        {
            return paymentMethod == PaymentMethodEnum.Card
                || paymentMethod == PaymentMethodEnum.Momo
                || paymentMethod == PaymentMethodEnum.OrangeMoney;
        }

        public static PaymentMethodEnum Normalize(PaymentMethodEnum paymentMethod)
        {
            return IsSupported(paymentMethod)
                ? paymentMethod
                : PaymentMethodEnum.Card;
        }
    }
}
