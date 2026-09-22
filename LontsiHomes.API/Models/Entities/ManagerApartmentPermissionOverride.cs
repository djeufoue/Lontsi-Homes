using System.ComponentModel.DataAnnotations;

namespace LontsiHomes.API.Models.Entities;

public class ManagerApartmentPermissionOverride
{
    [Key]
    public int Id { get; set; }
    public int PropertyManagerAssignmentId { get; set; }
    public PropertyManagerAssignment? PropertyManagerAssignment { get; set; }
    public int ApartmentId { get; set; }
    public Apartment? Apartment { get; set; }
    public bool HasAccess { get; set; }
    public long AllowedPermissionFlags { get; set; }
    public long DeniedPermissionFlags { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
