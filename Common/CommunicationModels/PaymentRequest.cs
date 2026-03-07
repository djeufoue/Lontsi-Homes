using Common.Enums;

namespace Common.CommunicationModels
{
    /// <summary>
    /// Request object used by the API to initiate a payment.  Contains all necessary
    /// information for mobile money and card transactions.
    /// </summary>
    public class PaymentRequest
    {
        /// <summary>
        /// Identifier of the tenant making the payment.  Uses string because user IDs are strings.
        /// </summary>
        public string TenantId { get; set; } = string.Empty;

        /// <summary>
        /// Identifier of the landlord receiving the payment.  Uses string because user IDs are strings.
        /// </summary>
        public string LandlordId { get; set; } = string.Empty;

        /// <summary>
        /// Amount to pay.  When paying rent, this value will be overridden by the tenancy's monthly rent
        /// multiplied by the number of periods specified.  It can still be used for other types of payments.
        /// </summary>
        public decimal Amount { get; set; }

        public PaymentMethodEnum Method { get; set; }

        public string TenantPhoneNumber { get; set; } = string.Empty;

        public string LandlordPhoneNumber { get; set; } = string.Empty;

        // Optional card details for card payments
        public string? CardNumber { get; set; }
        public string? CardExpiry { get; set; }
        public string? CardCvv { get; set; }

        /// <summary>
        /// Number of rent periods (months) the tenant wants to pay at once.  Defaults to 1.
        /// </summary>
        public int NumberOfPeriods { get; set; } = 1;
    }
}