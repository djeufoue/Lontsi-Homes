using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace RentHub.API.Models.Entities
{
    /// <summary>
    /// Links a user in the Owner role to a specific apartment.  Owners can be given
    /// read-only or read-write permissions.  Owners are not tenants; they simply manage
    /// an apartment on behalf of the landlord and may create tenancies if permitted.
    /// </summary>
    public class ApartmentOwner
    {
        [Key]
        public int Id { get; set; }

        public int ApartmentId { get; set; }
        public Apartment? Apartment { get; set; }

        public string OwnerId { get; set; } = string.Empty;
        public ApplicationUser? Owner { get; set; }

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