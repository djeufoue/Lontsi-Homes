using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Common.Enums;
using Microsoft.AspNetCore.Http;

namespace Common.CommunicationModels
{
    public class LandlordKycMediaDto
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public bool IsImage { get; set; }
        public string? Url { get; set; }
    }

    public class LandlordKycRejectedFilesDto
    {
        public bool FaceFront { get; set; }
        public bool FaceRight { get; set; }
        public bool FaceLeft { get; set; }
        public bool DocumentFront { get; set; }
        public bool DocumentBack { get; set; }

        public bool Any => FaceFront || FaceRight || FaceLeft || DocumentFront || DocumentBack;
    }

    public class LandlordKycSummaryDto
    {
        public bool HasProfile { get; set; }
        public KycDocumentTypeEnum? DocumentType { get; set; }
        public LandlordKycStatusEnum Status { get; set; } = LandlordKycStatusEnum.NotStarted;
        public DateTimeOffset? SubmittedAt { get; set; }
        public DateTimeOffset? ReviewedAt { get; set; }
        public string? ReviewedByName { get; set; }
        public string? ReviewNote { get; set; }
        public LandlordKycRejectedFilesDto RejectedFiles { get; set; } = new();
        public List<LandlordKycMediaDto> Media { get; set; } = new();

        public bool IsSubmitted => Status is LandlordKycStatusEnum.Submitted or LandlordKycStatusEnum.Approved;
        public bool IsApproved => Status == LandlordKycStatusEnum.Approved;
    }

    public class SubmitLandlordKycRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        public KycDocumentTypeEnum DocumentType { get; set; }

        public IFormFile? FaceFront { get; set; }
        public IFormFile? FaceRight { get; set; }
        public IFormFile? FaceLeft { get; set; }
        public IFormFile? DocumentFront { get; set; }
        public IFormFile? DocumentBack { get; set; }
    }

    public class SubmitLandlordContractRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        public bool Accepted { get; set; }

        [Required]
        [Display(Name = "Full legal name")]
        [StringLength(160, MinimumLength = 2)]
        public string SignatureName { get; set; } = string.Empty;
    }

    public class AdminLandlordApprovalDto
    {
        public string UserId { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public KycDocumentTypeEnum? DocumentType { get; set; }
        public LandlordKycStatusEnum KycStatus { get; set; }
        public DateTimeOffset? KycSubmittedAt { get; set; }
        public bool PlatformTermsAccepted { get; set; }
        public DateTimeOffset? PlatformTermsAcceptedAt { get; set; }
    }

    public class KycReviewRequest
    {
        [StringLength(500)]
        public string? Note { get; set; }

        public bool RejectAllFiles { get; set; }
        public bool RejectFaceFront { get; set; }
        public bool RejectFaceRight { get; set; }
        public bool RejectFaceLeft { get; set; }
        public bool RejectDocumentFront { get; set; }
        public bool RejectDocumentBack { get; set; }
    }
}
