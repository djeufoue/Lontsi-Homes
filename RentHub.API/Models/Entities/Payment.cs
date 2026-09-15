using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Common.Enums;
using Microsoft.EntityFrameworkCore;

namespace RentHub.API.Models.Entities
{
    /// <summary>
    /// Stores information about a payment from a tenant to a landlord.  Payments
    /// can be rent, deposits or subscription fees.
    /// </summary>
    [Index(nameof(RequestKey), IsUnique = true)]
    [Index(nameof(TransactionId), IsUnique = true)]
    [Index(nameof(CorrectionRequestId))]
    [Index(nameof(TenancyId), nameof(Status), nameof(PaymentDate))]
    public class Payment
    {
        [Key]
        public int Id { get; set; }
        public string TenantId { get; set; } = string.Empty;
        public ApplicationUser? Tenant { get; set; }
        public string LandlordId { get; set; } = string.Empty;
        public ApplicationUser? Landlord { get; set; }
        /// <summary>
        /// Optional reference to the tenancy for which this payment was made.  Used to
        /// associate rent payments with tenancy agreements.
        /// </summary>
        public int? TenancyId { get; set; }
        public Tenancy? Tenancy { get; set; }
        [Column(TypeName = "decimal(14,2)")]
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "XAF";
        public PaymentMethodEnum Method { get; set; }
        public string RequestKey { get; set; } = string.Empty;
        public string TransactionId { get; set; } = Guid.NewGuid().ToString();
        public string? ProviderReceiptUrl { get; set; }
        public string? SystemReceiptNumber { get; set; }
        public string? ReceiptVerificationCode { get; set; }
        public DateTimeOffset? ReceiptIssuedAt { get; set; }
        // Immutable receipt snapshots and actor/reason retained when a manual payment is corrected.
        public string? CorrectionJson { get; set; }
        [MaxLength(36)]
        public string? CorrectionRequestId { get; set; }
        public int? ReplacementPaymentId { get; set; }
        public DateTimeOffset? CorrectionTenantNotifiedAt { get; set; }
        public DateTimeOffset? CorrectionLandlordNotifiedAt { get; set; }
        public PaymentStatusEnum Status { get; set; } = PaymentStatusEnum.Pending;
        public DateTimeOffset PaymentDate { get; set; } = DateTimeOffset.UtcNow;

        // Audit fields
        public bool IsDeleted { get; set; } = false;
        public string? CreatedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string? UpdatedBy { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public string? DeletedBy { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
    }
}
