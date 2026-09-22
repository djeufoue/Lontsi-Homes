using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace LontsiHomes.API.Models.Entities
{
    public class TenancyTerminationRequest
    {
        [Key]
        public int Id { get; set; }
        public int TenancyId { get; set; }
        public Tenancy? Tenancy { get; set; }
        public string RequestedById { get; set; } = string.Empty;
        public ApplicationUser? RequestedBy { get; set; }
        public DateTimeOffset? OriginalEndDate { get; set; }
        public DateTimeOffset RequestedEndDate { get; set; }

        [MaxLength(1000)]
        public string? Reason { get; set; }

        public TenancyTerminationRequestStatusEnum Status { get; set; } = TenancyTerminationRequestStatusEnum.Pending;
        public string? ReviewedById { get; set; }
        public ApplicationUser? ReviewedBy { get; set; }
        public DateTimeOffset? ReviewedAt { get; set; }
        public DateTimeOffset? DecisionViewedAt { get; set; }

        [MaxLength(1000)]
        public string? RejectionReason { get; set; }

        public bool IsDeleted { get; set; }
        public string? CreatedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string? UpdatedBy { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public string? DeletedBy { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
    }
}
