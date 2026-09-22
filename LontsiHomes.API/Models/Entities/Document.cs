using System.ComponentModel.DataAnnotations;

using Common.Enums;

namespace LontsiHomes.API.Models.Entities
{
    /// <summary>
    /// Stores metadata about files uploaded by users.  Actual file content resides in
    /// Azure Blob Storage and is referenced via BlobUrl.
    /// </summary>
    public class Document
    {
        [Key]
        public int Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public ApplicationUser? User { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string BlobUrl { get; set; } = string.Empty;
        // Type of the document (image, contract, rules, etc.)
        public DocumentTypeEnum DocumentType { get; set; }
        public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;

        // Optional foreign keys to link the document to a property, apartment or tenancy
        public int? PropertyId { get; set; }
        public Property? Property { get; set; }
        public int? ApartmentId { get; set; }
        public Apartment? Apartment { get; set; }
        public int? TenancyId { get; set; }
        public Tenancy? Tenancy { get; set; }

        // Audit fields
        public bool IsDeleted { get; set; } = false;
        public string? CreatedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string? UpdatedBy { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public string? DeletedBy { get; set; }
        public virtual ApplicationUser? DeletedByUser { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
    }
}