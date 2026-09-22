using Common.Enums;

namespace LontsiHomes.API.Services.Permissions;

public interface IManagerPermissionService
{
    Task<bool> CanManageManagersAsync(string userId, int propertyId, bool isAdmin);
    Task<bool> HasPropertyPermissionAsync(string userId, int propertyId, ManagerPermission permission, bool isAdmin);
    Task<bool> HasApartmentPermissionAsync(string userId, int apartmentId, ManagerPermission permission, bool isAdmin);
    Task<bool> HasTenancyPermissionAsync(string userId, int tenancyId, ManagerPermission permission, bool isAdmin);
    Task<IReadOnlySet<int>> GetAccessibleApartmentIdsAsync(string userId, int propertyId, ManagerPermission permission, bool isAdmin);
}
