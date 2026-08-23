using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace Common.CommunicationModels
{
    public class CreateTenancyTerminationRequest
    {
        public DateTimeOffset RequestedEndDate { get; set; }

        [MaxLength(1000)]
        public string? Reason { get; set; }
    }

    public class RejectTenancyTerminationRequest
    {
        [Required]
        [MaxLength(1000)]
        public string Reason { get; set; } = string.Empty;
    }

    public class CancelTenancyTerminationRequest
    {
        public TenancyTerminationCancellationModeEnum Mode { get; set; }
        public DateTimeOffset? ReplacementStartDate { get; set; }
        public DateTimeOffset? NewEndDate { get; set; }

        [MaxLength(1000)]
        public string? Note { get; set; }
    }

    public class TenancyTerminationRequestDto
    {
        public int Id { get; set; }
        public int TenancyId { get; set; }
        public string RequestedById { get; set; } = string.Empty;
        public string RequestedByName { get; set; } = string.Empty;
        public DateTimeOffset? OriginalEndDate { get; set; }
        public DateTimeOffset RequestedEndDate { get; set; }
        public string? Reason { get; set; }
        public TenancyTerminationRequestStatusEnum Status { get; set; }
        public string? ReviewedByName { get; set; }
        public DateTimeOffset? ReviewedAt { get; set; }
        public string? RejectionReason { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public bool IsDirectDecision { get; set; }
        public bool CanCancel { get; set; }
    }

    public class TenancyRequestItemDto
    {
        public int Id { get; set; }
        public int TenancyId { get; set; }
        public int ApartmentId { get; set; }
        public int PropertyId { get; set; }
        public TenancyRequestTypeEnum Type { get; set; }
        public string Status { get; set; } = string.Empty;
        public string PropertyName { get; set; } = string.Empty;
        public string ApartmentName { get; set; } = string.Empty;
        public string RequestedById { get; set; } = string.Empty;
        public string RequestedByName { get; set; } = string.Empty;
        public DateTimeOffset RequestedAt { get; set; }
        public DateTimeOffset RequestedDate { get; set; }
        public DateTimeOffset? PreviousEndDate { get; set; }
        public string? RequestReason { get; set; }
        public string? ReviewedByName { get; set; }
        public DateTimeOffset? ReviewedAt { get; set; }
        public string? RejectionReason { get; set; }
        public bool CanReview { get; set; }
        public bool CanWithdraw { get; set; }
        public bool CanCancel { get; set; }
        public bool IsDirectDecision { get; set; }
    }

    public class TenancyRequestPendingCountDto
    {
        public int Count { get; set; }
        public bool ShowsDecisionResponses { get; set; }
    }

    public class TenancyRequestListDto
    {
        public List<TenancyRequestItemDto> Items { get; set; } = new();
        public List<TenancyRequestPropertyOptionDto> Properties { get; set; } = new();
        public List<TenancyRequestApartmentOptionDto> Apartments { get; set; } = new();
        public List<TenancyRequestContextOptionDto> RequestableTenancies { get; set; } = new();
        public int? PropertyId { get; set; }
        public int? ApartmentId { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 12;
        public int TotalItems { get; set; }
        public int TotalPages { get; set; } = 1;
    }

    public class TenancyRequestPropertyOptionDto
    {
        public int Id { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    public class TenancyRequestApartmentOptionDto
    {
        public int Id { get; set; }
        public int PropertyId { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    public class TenancyRequestContextOptionDto
    {
        public int TenancyId { get; set; }
        public int PropertyId { get; set; }
        public int ApartmentId { get; set; }
        public string PropertyName { get; set; } = string.Empty;
        public string ApartmentName { get; set; } = string.Empty;
        public DateTimeOffset? EndDate { get; set; }
    }
}
