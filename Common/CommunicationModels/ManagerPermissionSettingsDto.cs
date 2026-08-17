using System.Collections.Generic;

namespace Common.CommunicationModels;

public class ManagerPermissionSettingsDto
{
    public int AssignmentId { get; set; }
    public int PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string ManagerId { get; set; } = string.Empty;
    public string ManagerName { get; set; } = string.Empty;
    public string ManagerEmail { get; set; } = string.Empty;
    public long PermissionFlags { get; set; }
    public bool AccessAllApartments { get; set; }
    public List<ManagerApartmentPermissionDto> Apartments { get; set; } = new();
}

public class ManagerApartmentPermissionDto
{
    public int ApartmentId { get; set; }
    public string ApartmentName { get; set; } = string.Empty;
    public bool HasAccess { get; set; }
    public long AllowedPermissionFlags { get; set; }
    public long DeniedPermissionFlags { get; set; }
}

public class UpdateManagerPermissionSettingsRequest
{
    public long PermissionFlags { get; set; }
    public bool AccessAllApartments { get; set; }
    public List<ManagerApartmentPermissionDto> Apartments { get; set; } = new();
}
