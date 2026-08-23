using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace Common.CommunicationModels
{
    public class RejectTenancyExtensionRequest
    {
        [Required]
        [MaxLength(512)]
        public string Reason { get; set; } = string.Empty;
    }

    public class TenancyExtensionRequestDto
    {
        public int Id { get; set; }
        public int TenancyId { get; set; }
        public string RequestedById { get; set; } = string.Empty;
        public string RequestedByName { get; set; } = string.Empty;
        public DateTimeOffset? OriginalEndDate { get; set; }
        public DateTimeOffset ProposedEndDate { get; set; }
        public TenancyExtensionStatusEnum Status { get; set; }
        public string? ReviewedByName { get; set; }
        public DateTimeOffset? ReviewedAt { get; set; }
        public string? RejectionReason { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    public class TenancyRenewalWorkspaceDto
    {
        public int TenancyId { get; set; }
        public string PropertyName { get; set; } = string.Empty;
        public string ApartmentName { get; set; } = string.Empty;
        public DateTimeOffset StartDate { get; set; }
        public DateTimeOffset? CurrentEndDate { get; set; }
        public bool IsTerminated { get; set; }
        public bool IsExpired { get; set; }
        public bool CanRequest { get; set; }
        public bool CanReview { get; set; }
        public string RequestUnavailableReason { get; set; } = string.Empty;
        public DateTimeOffset? MinimumProposedEndDate { get; set; }
        public List<TenancyExtensionRequestDto> Requests { get; set; } = new();
    }
}
