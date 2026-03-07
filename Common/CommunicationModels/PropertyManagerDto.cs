using System;
using Common.Enums;

namespace Common.CommunicationModels
{
    /// <summary>
    /// Represents the assignment of a manager to a property including permission level.
    /// </summary>
    public class PropertyManagerDto
    {
        public int Id { get; set; }
        public string ManagerId { get; set; } = string.Empty;
        public string ManagerName { get; set; } = string.Empty;
        public PermissionLevelEnum Permission { get; set; }
        public DateTimeOffset AssignedAt { get; set; }
    }
}