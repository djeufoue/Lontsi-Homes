using Common.Enums;

namespace LontsiHomes.API.Models.Entities
{
    public class LandlordKycProfile
    {
        public int Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public ApplicationUser? User { get; set; }

        public KycDocumentTypeEnum DocumentType { get; set; }
        public LandlordKycStatusEnum Status { get; set; } = LandlordKycStatusEnum.Submitted;

        public string FaceFrontPath { get; set; } = string.Empty;
        public string FaceFrontContentType { get; set; } = string.Empty;
        public string FaceFrontOriginalFileName { get; set; } = string.Empty;

        public string FaceRightPath { get; set; } = string.Empty;
        public string FaceRightContentType { get; set; } = string.Empty;
        public string FaceRightOriginalFileName { get; set; } = string.Empty;

        public string FaceLeftPath { get; set; } = string.Empty;
        public string FaceLeftContentType { get; set; } = string.Empty;
        public string FaceLeftOriginalFileName { get; set; } = string.Empty;

        public string DocumentFrontPath { get; set; } = string.Empty;
        public string DocumentFrontContentType { get; set; } = string.Empty;
        public string DocumentFrontOriginalFileName { get; set; } = string.Empty;

        public string? DocumentBackPath { get; set; }
        public string? DocumentBackContentType { get; set; }
        public string? DocumentBackOriginalFileName { get; set; }

        public bool RejectFaceFront { get; set; }
        public bool RejectFaceRight { get; set; }
        public bool RejectFaceLeft { get; set; }
        public bool RejectDocumentFront { get; set; }
        public bool RejectDocumentBack { get; set; }

        public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;
        public string? ReviewedById { get; set; }
        public ApplicationUser? ReviewedBy { get; set; }
        public DateTimeOffset? ReviewedAt { get; set; }
        public string? ReviewNote { get; set; }

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? UpdatedAt { get; set; }
    }
}
