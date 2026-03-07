using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Common.Enums;

namespace RentHub.API.Models.Entities
{
    /// <summary>
    /// Stores information about a payment from a tenant to a landlord.  Payments
    /// can be rent, deposits or subscription fees.
    /// </summary>
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
        public string TransactionId { get; set; } = Guid.NewGuid().ToString();
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