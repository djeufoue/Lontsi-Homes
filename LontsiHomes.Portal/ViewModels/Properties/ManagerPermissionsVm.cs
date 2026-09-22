using Common.CommunicationModels;

namespace LontsiHomes.Portal.ViewModels.Properties;

public class ManagerPermissionsVm
{
    public int PropertyId { get; set; }
    public int AssignmentId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string ManagerName { get; set; } = string.Empty;
    public string ManagerEmail { get; set; } = string.Empty;
    public bool AccessAllApartments { get; set; }
    public List<long> SelectedPermissions { get; set; } = new();
    public List<ManagerApartmentPermissionsVm> Apartments { get; set; } = new();
}

public class ManagerApartmentPermissionsVm
{
    public int ApartmentId { get; set; }
    public string ApartmentName { get; set; } = string.Empty;
    public bool HasAccess { get; set; }
    public List<long> SelectedPermissions { get; set; } = new();
}
