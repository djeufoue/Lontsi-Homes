using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace RentHub.API.Models.Entities
{
    /// <summary>
    /// Associates a user in the Manager role with a property.  Property managers can be
    /// given read-only or read-write permissions across all apartments in the property.
    /// </summary>
    public class PropertyManagerAssignment
    {
        [Key]
        public int Id { get; set; }

        public int PropertyId { get; set; }
        public Property? Property { get; set; }

        public string ManagerId { get; set; } = string.Empty;
        public ApplicationUser? Manager { get; set; }

        public PermissionLevelEnum Permission { get; set; } = PermissionLevelEnum.ReadOnly;

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