using System.ComponentModel.DataAnnotations;

namespace RentHub.API.Models.Entities;

public class ManagerPermissionAuditLog
{
    [Key]
    public long Id { get; set; }
    public int PropertyId { get; set; }
    public int PropertyManagerAssignmentId { get; set; }
    [MaxLength(450)] public string ManagerId { get; set; } = string.Empty;
    [MaxLength(450)] public string ChangedBy { get; set; } = string.Empty;
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public long PreviousPermissionFlags { get; set; }
    public long NewPermissionFlags { get; set; }
    public bool PreviousAccessAllApartments { get; set; }
    public bool NewAccessAllApartments { get; set; }
    [MaxLength(4000)] public string Details { get; set; } = string.Empty;
}
