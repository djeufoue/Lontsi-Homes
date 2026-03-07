using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace RentHub.API.Models.Entities
{
    /// <summary>
    /// Associates multiple users with a tenancy (e.g., joint tenants).  Each member
    /// has a role indicating their responsibility.
    /// </summary>
    public class TenancyMember
    {
        [Key]
        public int Id { get; set; }

        public int TenancyId { get; set; }
        public Tenancy? Tenancy { get; set; }

        public string MemberId { get; set; } = string.Empty;
        public ApplicationUser? Member { get; set; }

        public TenancyMemberRoleEnum Role { get; set; } = TenancyMemberRoleEnum.CoTenant;

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