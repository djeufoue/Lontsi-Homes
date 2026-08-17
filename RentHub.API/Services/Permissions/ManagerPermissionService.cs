using Common.Enums;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;

namespace RentHub.API.Services.Permissions;

public sealed class ManagerPermissionService : IManagerPermissionService
{
    private readonly ApplicationDbContext _context;

    public ManagerPermissionService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> CanManageManagersAsync(string userId, int propertyId, bool isAdmin)
    {
        return isAdmin || await _context.Properties.AnyAsync(property =>
            property.Id == propertyId && property.LandlordId == userId);
    }

    public async Task<bool> HasPropertyPermissionAsync(
        string userId,
        int propertyId,
        ManagerPermission permission,
        bool isAdmin)
    {
        if (isAdmin || await IsLandlordAsync(userId, propertyId))
        {
            return true;
        }

        var flags = await _context.PropertyManagerAssignments
            .Where(assignment => assignment.PropertyId == propertyId && assignment.ManagerId == userId)
            .Select(assignment => (long?)assignment.PermissionFlags)
            .FirstOrDefaultAsync();

        return flags.HasValue && Has(flags.Value, permission);
    }

    public async Task<bool> HasApartmentPermissionAsync(
        string userId,
        int apartmentId,
        ManagerPermission permission,
        bool isAdmin)
    {
        var apartment = await _context.Apartments
            .AsNoTracking()
            .Where(item => item.Id == apartmentId)
            .Select(item => new { item.PropertyId, item.Property!.LandlordId })
            .FirstOrDefaultAsync();

        if (apartment == null)
        {
            return false;
        }

        if (isAdmin || apartment.LandlordId == userId)
        {
            return true;
        }

        var assignment = await _context.PropertyManagerAssignments
            .AsNoTracking()
            .Where(item => item.PropertyId == apartment.PropertyId && item.ManagerId == userId)
            .Select(item => new
            {
                item.Id,
                item.PermissionFlags,
                item.AccessAllApartments
            })
            .FirstOrDefaultAsync();

        if (assignment != null)
        {
            var apartmentOverride = await _context.ManagerApartmentPermissionOverrides
                .AsNoTracking()
                .FirstOrDefaultAsync(item =>
                    item.PropertyManagerAssignmentId == assignment.Id && item.ApartmentId == apartmentId);

            var hasAccess = apartmentOverride?.HasAccess ?? assignment.AccessAllApartments;
            if (!hasAccess)
            {
                return false;
            }

            var effectiveFlags = assignment.PermissionFlags;
            if (apartmentOverride != null)
            {
                effectiveFlags |= apartmentOverride.AllowedPermissionFlags;
                effectiveFlags &= ~apartmentOverride.DeniedPermissionFlags;
            }

            return Has(effectiveFlags, permission);
        }

        // Existing apartment-owner permissions remain supported and are deliberately
        // separate from the Manager permission model.
        var ownerPermission = await _context.ApartmentOwners
            .Where(item => item.ApartmentId == apartmentId && item.OwnerId == userId)
            .Select(item => (PermissionLevelEnum?)item.Permission)
            .FirstOrDefaultAsync();

        return ownerPermission.HasValue &&
               (IsReadPermission(permission) || ownerPermission == PermissionLevelEnum.ReadWrite);
    }

    public async Task<bool> HasTenancyPermissionAsync(
        string userId,
        int tenancyId,
        ManagerPermission permission,
        bool isAdmin)
    {
        var apartmentId = await _context.Tenancies
            .Where(tenancy => tenancy.Id == tenancyId)
            .Select(tenancy => (int?)tenancy.ApartmentId)
            .FirstOrDefaultAsync();

        return apartmentId.HasValue &&
               await HasApartmentPermissionAsync(userId, apartmentId.Value, permission, isAdmin);
    }

    public async Task<IReadOnlySet<int>> GetAccessibleApartmentIdsAsync(
        string userId,
        int propertyId,
        ManagerPermission permission,
        bool isAdmin)
    {
        var apartmentIds = await _context.Apartments
            .Where(apartment => apartment.PropertyId == propertyId)
            .Select(apartment => apartment.Id)
            .ToListAsync();

        if (isAdmin || await IsLandlordAsync(userId, propertyId))
        {
            return apartmentIds.ToHashSet();
        }

        var result = new HashSet<int>();
        foreach (var apartmentId in apartmentIds)
        {
            if (await HasApartmentPermissionAsync(userId, apartmentId, permission, false))
            {
                result.Add(apartmentId);
            }
        }

        return result;
    }

    private Task<bool> IsLandlordAsync(string userId, int propertyId)
    {
        return _context.Properties.AnyAsync(property => property.Id == propertyId && property.LandlordId == userId);
    }

    private static bool Has(long flags, ManagerPermission permission)
    {
        var required = (long)permission;
        return required != 0 && (flags & required) == required;
    }

    private static bool IsReadPermission(ManagerPermission permission)
    {
        return permission is ManagerPermission.ViewProperty
            or ManagerPermission.ViewPropertyOverview
            or ManagerPermission.ViewPropertyFinancialInformation
            or ManagerPermission.ViewMembers
            or ManagerPermission.ViewManagers
            or ManagerPermission.ViewApartments
            or ManagerPermission.ViewApartmentDetails
            or ManagerPermission.ViewApartmentFinancialInformation
            or ManagerPermission.ViewTenancies
            or ManagerPermission.ViewTenancyDetails
            or ManagerPermission.ViewTenancyMembers
            or ManagerPermission.ViewLeaseDocuments
            or ManagerPermission.ViewRentInformation
            or ManagerPermission.ViewRentPaymentHistory
            or ManagerPermission.ViewOutstandingRent
            or ManagerPermission.ViewDocuments
            or ManagerPermission.ViewMessages
            or ManagerPermission.ViewDashboard;
    }
}
