using System;
using Common.Enums;

namespace Common.CommunicationModels
{
    /// <summary>
    /// Represents a stored document in the system.  Exposes only metadata that
    /// clients need such as the blob URL and associated entity identifiers.
    /// </summary>
    public class DocumentDto
    {
        public int Id { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string BlobUrl { get; set; } = string.Empty;
        public DocumentTypeEnum DocumentType { get; set; }
        public DateTimeOffset UploadedAt { get; set; }
        public int? PropertyId { get; set; }
        public int? ApartmentId { get; set; }
        public int? TenancyId { get; set; }
    }
}