using System.ComponentModel.DataAnnotations;

using Common.Enums;

namespace LontsiHomes.API.Models.Entities
{
    /// <summary>
    /// Represents a request by a tenant to extend their tenancy end date.  Requests must
    /// be approved or rejected by an authorized manager or landlord.
    /// </summary>
    public class TenancyExtensionRequest
    {
        [Key]
        public int Id { get; set; }
        public int TenancyId { get; set; }
        public Tenancy? Tenancy { get; set; }
        public string RequestedById { get; set; } = string.Empty;
        public ApplicationUser? RequestedBy { get; set; }
        public DateTimeOffset? OriginalEndDate { get; set; }
        public DateTimeOffset ProposedEndDate { get; set; }
        public TenancyExtensionStatusEnum Status { get; set; } = TenancyExtensionStatusEnum.Pending;
        public string? ApprovedById { get; set; }
        public ApplicationUser? ApprovedBy { get; set; }
        public DateTimeOffset? ApprovedAt { get; set; }
        public DateTimeOffset? DecisionViewedAt { get; set; }
        [MaxLength(512)]
        public string? RejectionReason { get; set; }
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
