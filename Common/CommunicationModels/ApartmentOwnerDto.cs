using System;
using Common.Enums;

namespace Common.CommunicationModels
{
    /// <summary>
    /// Represents the assignment of an owner to an apartment along with permission level.
    /// </summary>
    public class ApartmentOwnerDto
    {
        public int Id { get; set; }
        public string OwnerId { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;
        public PermissionLevelEnum Permission { get; set; }
        public DateTimeOffset AssignedAt { get; set; }
    }
}