using System;
using Common.Enums;

namespace Common.CommunicationModels
{
    /// <summary>
    /// Represents an apartment-level member assignment along with permission level.
    /// </summary>
    public class ApartmentOwnerDto
    {
        public int Id { get; set; }
        public string OwnerId { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;
        public string OwnerEmail { get; set; } = string.Empty;
        public bool CanEditEmail { get; set; }
        public ApartmentMemberRoleEnum Role { get; set; }
        public PermissionLevelEnum Permission { get; set; }
        public DateTimeOffset AssignedAt { get; set; }
    }
}
