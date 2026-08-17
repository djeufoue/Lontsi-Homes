using Common.CommunicationModels;
using Common.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Helpers;
using RentHub.API.Services.Permissions;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/workspace-directory")]
    [Authorize(Roles = "Admin,Landlord,Manager,Tenant")]
    public class WorkspaceDirectoryController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IManagerPermissionService _permissionService;

        public WorkspaceDirectoryController(ApplicationDbContext context, IManagerPermissionService permissionService)
        {
            _context = context;
            _permissionService = permissionService;
        }

        [HttpGet("apartments")]
        public async Task<IActionResult> GetApartments(
            [FromQuery] string? search = null,
            [FromQuery] int? propertyId = null,
            [FromQuery] string? status = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 12)
        {
            var scope = await LoadPropertyScopeAsync();
            if (scope == null) return Unauthorized();

            var userId = UserHelpers.GetUserId(User)!;
            var restrictToTenantAssignments = IsRestrictedTenant();
            var propertyIds = scope.Select(property => property.Id).ToList();
            var apartments = await _context.Apartments
                .AsNoTracking()
                .Include(apartment => apartment.Property)
                    .ThenInclude(property => property!.Landlord)
                .Include(apartment => apartment.Tenancies)
                    .ThenInclude(tenancy => tenancy.Members)
                .Where(apartment =>
                    !apartment.IsDeleted &&
                    propertyIds.Contains(apartment.PropertyId) &&
                    (!restrictToTenantAssignments || apartment.Tenancies.Any(tenancy =>
                        !tenancy.IsDeleted && tenancy.Members.Any(member => !member.IsDeleted && member.MemberId == userId))))
                .OrderByDescending(apartment => apartment.CreatedAt)
                .ToListAsync();

            if (User.IsInRole("Manager") && !User.IsInRole("Admin"))
            {
                var accessibleIds = await GetAccessibleApartmentIdsForScopeAsync(
                    userId, propertyIds, ManagerPermission.ViewApartments);
                apartments = apartments.Where(apartment => accessibleIds.Contains(apartment.Id)).ToList();
            }

            var now = DateTimeOffset.UtcNow;
            var rows = apartments.Select(apartment =>
            {
                var activeTenancies = apartment.Tenancies.Count(tenancy => IsActiveTenancy(tenancy.StartDate, tenancy.EndDate, tenancy.EndBehavior, tenancy.TerminatedAt, now));
                return new WorkspaceApartmentDto
                {
                    Id = apartment.Id,
                    PropertyId = apartment.PropertyId,
                    Name = apartment.Name,
                    PropertyName = apartment.Property?.Name ?? string.Empty,
                    LandlordName = apartment.Property?.Landlord?.FullName ?? apartment.Property?.Landlord?.Email ?? string.Empty,
                    Type = apartment.Type.ToString(),
                    Status = ApartmentStatusResolver.Resolve(apartment.Tenancies, now).ToString(),
                    Rent = apartment.Price,
                    Area = apartment.Area,
                    ActiveTenancies = activeTenancies,
                    MemberCount = apartment.Tenancies
                        .Where(tenancy => !tenancy.IsDeleted)
                        .SelectMany(tenancy => tenancy.Members)
                        .Count(member => !member.IsDeleted)
                };
            }).ToList();

            var allStatuses = rows.Select(row => row.Status).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToList();
            var filtered = rows.AsEnumerable();
            if (propertyId.HasValue) filtered = filtered.Where(row => row.PropertyId == propertyId.Value);
            if (!string.IsNullOrWhiteSpace(status)) filtered = filtered.Where(row => row.Status.Equals(status.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                filtered = filtered.Where(row => Contains(row.Name, term) || Contains(row.PropertyName, term) || Contains(row.LandlordName, term) || Contains(row.Type, term));
            }

            return Ok(BuildResponse(filtered, rows.Count, scope, search, propertyId, status, null, allStatuses, null, page, pageSize));
        }

        [HttpGet("tenancies")]
        public async Task<IActionResult> GetTenancies(
            [FromQuery] string? search = null,
            [FromQuery] int? propertyId = null,
            [FromQuery] string? status = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 12)
        {
            var scope = await LoadPropertyScopeAsync();
            if (scope == null) return Unauthorized();

            var userId = UserHelpers.GetUserId(User)!;
            var restrictToTenantAssignments = IsRestrictedTenant();
            var propertyIds = scope.Select(property => property.Id).ToList();
            var tenancies = await _context.Tenancies
                .AsNoTracking()
                .Include(tenancy => tenancy.Apartment)
                    .ThenInclude(apartment => apartment!.Property)
                    .ThenInclude(property => property!.Landlord)
                .Include(tenancy => tenancy.Members)
                .Where(tenancy =>
                    !tenancy.IsDeleted &&
                    tenancy.Apartment != null &&
                    propertyIds.Contains(tenancy.Apartment.PropertyId) &&
                    (!restrictToTenantAssignments || tenancy.Members.Any(member => !member.IsDeleted && member.MemberId == userId)))
                .OrderByDescending(tenancy => tenancy.CreatedAt)
                .ToListAsync();

            if (User.IsInRole("Manager") && !User.IsInRole("Admin"))
            {
                var accessibleIds = await GetAccessibleApartmentIdsForScopeAsync(
                    userId, propertyIds, ManagerPermission.ViewTenancies);
                tenancies = tenancies.Where(tenancy => accessibleIds.Contains(tenancy.ApartmentId)).ToList();
            }

            var now = DateTimeOffset.UtcNow;
            var rows = tenancies.Select(tenancy => new WorkspaceTenancyDto
            {
                Id = tenancy.Id,
                ApartmentId = tenancy.ApartmentId,
                PropertyId = tenancy.Apartment!.PropertyId,
                PropertyName = tenancy.Apartment.Property?.Name ?? string.Empty,
                ApartmentName = tenancy.Apartment.Name,
                LandlordName = tenancy.Apartment.Property?.Landlord?.FullName ?? tenancy.Apartment.Property?.Landlord?.Email ?? string.Empty,
                Status = ResolveTenancyStatus(tenancy.StartDate, tenancy.EndDate, tenancy.EndBehavior, tenancy.TerminatedAt, now),
                StartDate = tenancy.StartDate,
                EndDate = tenancy.EndDate,
                MonthlyRent = tenancy.MonthlyRent,
                MemberCount = tenancy.Members.Count(member => !member.IsDeleted),
                MaxMembers = tenancy.MaxMembers
            }).ToList();

            var allStatuses = rows.Select(row => row.Status).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToList();
            var filtered = rows.AsEnumerable();
            if (propertyId.HasValue) filtered = filtered.Where(row => row.PropertyId == propertyId.Value);
            if (!string.IsNullOrWhiteSpace(status)) filtered = filtered.Where(row => row.Status.Equals(status.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                filtered = filtered.Where(row => Contains(row.PropertyName, term) || Contains(row.ApartmentName, term) || Contains(row.LandlordName, term) || Contains(row.Status, term));
            }

            return Ok(BuildResponse(filtered, rows.Count, scope, search, propertyId, status, null, allStatuses, null, page, pageSize));
        }

        [HttpGet("members")]
        [Authorize(Roles = "Admin,Landlord,Manager")]
        public async Task<IActionResult> GetMembers(
            [FromQuery] string? search = null,
            [FromQuery] int? propertyId = null,
            [FromQuery] string? role = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 12)
        {
            var scope = await LoadPropertyScopeAsync();
            if (scope == null) return Unauthorized();

            var propertyIds = scope.Select(property => property.Id).ToList();
            var rows = new List<WorkspaceMemberDto>();
            var userId = UserHelpers.GetUserId(User)!;
            var restrictedManager = User.IsInRole("Manager") && !User.IsInRole("Admin");
            var managerPropertyIds = propertyIds.ToHashSet();
            var memberApartmentIds = new HashSet<int>();
            if (restrictedManager)
            {
                managerPropertyIds.Clear();
                foreach (var scopedPropertyId in propertyIds)
                {
                    if (await _permissionService.HasPropertyPermissionAsync(
                            userId, scopedPropertyId, ManagerPermission.ViewManagers, false))
                    {
                        managerPropertyIds.Add(scopedPropertyId);
                    }
                }

                memberApartmentIds = await GetAccessibleApartmentIdsForScopeAsync(
                    userId, propertyIds, ManagerPermission.ViewMembers);
            }

            var managers = await _context.PropertyManagerAssignments
                .AsNoTracking()
                .Include(assignment => assignment.Manager)
                .Include(assignment => assignment.Property)
                    .ThenInclude(property => property!.Landlord)
                .Where(assignment => !assignment.IsDeleted && managerPropertyIds.Contains(assignment.PropertyId))
                .ToListAsync();
            rows.AddRange(managers.Select(assignment => new WorkspaceMemberDto
            {
                AssignmentKey = $"property-{assignment.Id}",
                UserId = assignment.ManagerId,
                FullName = assignment.Manager?.FullName ?? assignment.Manager?.Email ?? string.Empty,
                Email = assignment.Manager?.Email ?? string.Empty,
                Role = "Property Manager",
                Level = "Property",
                Permission = assignment.Permission.ToString(),
                Status = assignment.Manager?.EmailConfirmed == true ? "Confirmed" : "Pending",
                PropertyId = assignment.PropertyId,
                PropertyName = assignment.Property?.Name ?? string.Empty,
                LandlordName = assignment.Property?.Landlord?.FullName ?? assignment.Property?.Landlord?.Email ?? string.Empty,
                AssignedAt = assignment.CreatedAt
            }));

            var apartmentMembers = await _context.ApartmentOwners
                .AsNoTracking()
                .Include(assignment => assignment.Owner)
                .Include(assignment => assignment.Apartment)
                    .ThenInclude(apartment => apartment!.Property)
                    .ThenInclude(property => property!.Landlord)
                .Where(assignment => !assignment.IsDeleted && assignment.Apartment != null && propertyIds.Contains(assignment.Apartment.PropertyId))
                .ToListAsync();
            if (restrictedManager)
            {
                apartmentMembers = apartmentMembers.Where(assignment => memberApartmentIds.Contains(assignment.ApartmentId)).ToList();
            }
            rows.AddRange(apartmentMembers.Select(assignment => new WorkspaceMemberDto
            {
                AssignmentKey = $"apartment-{assignment.Id}",
                UserId = assignment.OwnerId,
                FullName = assignment.Owner?.FullName ?? assignment.Owner?.Email ?? string.Empty,
                Email = assignment.Owner?.Email ?? string.Empty,
                Role = assignment.Role.ToString(),
                Level = "Apartment",
                Permission = assignment.Permission.ToString(),
                Status = assignment.Owner?.EmailConfirmed == true ? "Confirmed" : "Pending",
                PropertyId = assignment.Apartment!.PropertyId,
                PropertyName = assignment.Apartment.Property?.Name ?? string.Empty,
                ApartmentId = assignment.ApartmentId,
                ApartmentName = assignment.Apartment.Name,
                LandlordName = assignment.Apartment.Property?.Landlord?.FullName ?? assignment.Apartment.Property?.Landlord?.Email ?? string.Empty,
                AssignedAt = assignment.CreatedAt
            }));

            var tenancyMembers = await _context.TenancyMembers
                .AsNoTracking()
                .Include(assignment => assignment.Member)
                .Include(assignment => assignment.Tenancy)
                    .ThenInclude(tenancy => tenancy!.Apartment)
                    .ThenInclude(apartment => apartment!.Property)
                    .ThenInclude(property => property!.Landlord)
                .Where(assignment => !assignment.IsDeleted && assignment.Tenancy != null && assignment.Tenancy.Apartment != null && propertyIds.Contains(assignment.Tenancy.Apartment.PropertyId))
                .ToListAsync();
            if (restrictedManager)
            {
                tenancyMembers = tenancyMembers.Where(assignment => memberApartmentIds.Contains(assignment.Tenancy!.ApartmentId)).ToList();
            }
            rows.AddRange(tenancyMembers.Select(assignment => new WorkspaceMemberDto
            {
                AssignmentKey = $"tenancy-{assignment.Id}",
                UserId = assignment.MemberId,
                FullName = assignment.Member?.FullName ?? assignment.Member?.Email ?? string.Empty,
                Email = assignment.Member?.Email ?? string.Empty,
                Role = assignment.Role.ToString(),
                Level = "Tenancy",
                Permission = "Occupant",
                Status = assignment.Member?.EmailConfirmed == true ? "Confirmed" : "Pending",
                PropertyId = assignment.Tenancy!.Apartment!.PropertyId,
                PropertyName = assignment.Tenancy.Apartment.Property?.Name ?? string.Empty,
                ApartmentId = assignment.Tenancy.ApartmentId,
                ApartmentName = assignment.Tenancy.Apartment.Name,
                TenancyId = assignment.TenancyId,
                LandlordName = assignment.Tenancy.Apartment.Property?.Landlord?.FullName ?? assignment.Tenancy.Apartment.Property?.Landlord?.Email ?? string.Empty,
                AssignedAt = assignment.CreatedAt
            }));

            rows = rows.OrderByDescending(row => row.AssignedAt).ToList();
            var allRoles = rows.Select(row => row.Role).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToList();
            var filtered = rows.AsEnumerable();
            if (propertyId.HasValue) filtered = filtered.Where(row => row.PropertyId == propertyId.Value);
            if (!string.IsNullOrWhiteSpace(role)) filtered = filtered.Where(row => row.Role.Equals(role.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                filtered = filtered.Where(row => Contains(row.FullName, term) || Contains(row.Email, term) || Contains(row.PropertyName, term) || Contains(row.ApartmentName, term) || Contains(row.Role, term));
            }

            return Ok(BuildResponse(filtered, rows.Count, scope, search, propertyId, null, role, null, allRoles, page, pageSize));
        }

        private async Task<List<PropertyScopeRow>?> LoadPropertyScopeAsync()
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return null;

            var query = _context.Properties.AsNoTracking().Where(property => !property.IsDeleted);
            if (!User.IsInRole("Admin"))
            {
                query = query.Where(property =>
                    property.LandlordId == userId ||
                    _context.PropertyManagerAssignments.Any(assignment =>
                        !assignment.IsDeleted && assignment.PropertyId == property.Id && assignment.ManagerId == userId) ||
                    _context.ApartmentOwners.Any(assignment =>
                        !assignment.IsDeleted &&
                        assignment.OwnerId == userId &&
                        assignment.Apartment != null &&
                        assignment.Apartment.PropertyId == property.Id) ||
                    _context.TenancyMembers.Any(assignment =>
                        !assignment.IsDeleted &&
                        assignment.MemberId == userId &&
                        assignment.Tenancy != null &&
                        assignment.Tenancy.Apartment != null &&
                        assignment.Tenancy.Apartment.PropertyId == property.Id));
            }

            return await query
                .OrderBy(property => property.Name)
                .Select(property => new PropertyScopeRow(property.Id, property.Name))
                .ToListAsync();
        }

        private async Task<HashSet<int>> GetAccessibleApartmentIdsForScopeAsync(
            string userId,
            IEnumerable<int> propertyIds,
            ManagerPermission permission)
        {
            var result = new HashSet<int>();
            foreach (var scopedPropertyId in propertyIds)
            {
                result.UnionWith(await _permissionService.GetAccessibleApartmentIdsAsync(
                    userId, scopedPropertyId, permission, User.IsInRole("Admin")));
            }

            return result;
        }

        private static WorkspaceDirectoryResponseDto<T> BuildResponse<T>(
            IEnumerable<T> filtered,
            int scopeTotal,
            IReadOnlyCollection<PropertyScopeRow> properties,
            string? search,
            int? propertyId,
            string? status,
            string? role,
            List<string>? statuses,
            List<string>? roles,
            int page,
            int pageSize)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 6, 50);
            var materialized = filtered.ToList();
            var total = materialized.Count;
            var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
            page = Math.Min(page, totalPages);

            return new WorkspaceDirectoryResponseDto<T>
            {
                Items = materialized.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
                Properties = properties.Select(property => new WorkspaceFilterOptionDto { Id = property.Id, Label = property.Name }).ToList(),
                Statuses = statuses ?? new List<string>(),
                Roles = roles ?? new List<string>(),
                Search = search?.Trim() ?? string.Empty,
                PropertyId = propertyId,
                Status = status?.Trim() ?? string.Empty,
                Role = role?.Trim() ?? string.Empty,
                Page = page,
                PageSize = pageSize,
                TotalCount = total,
                ScopeTotalCount = scopeTotal
            };
        }

        private static bool Contains(string value, string term) => value.Contains(term, StringComparison.OrdinalIgnoreCase);

        private bool IsRestrictedTenant()
        {
            return User.IsInRole("Tenant") &&
                   !User.IsInRole("Admin") &&
                   !User.IsInRole("Landlord") &&
                   !User.IsInRole("Manager");
        }

        private static bool IsActiveTenancy(
            DateTimeOffset startDate,
            DateTimeOffset? endDate,
            TenancyEndBehaviorEnum endBehavior,
            DateTimeOffset? terminatedAt,
            DateTimeOffset now)
        {
            if (terminatedAt.HasValue || startDate.Date > now.Date) return false;
            return !endDate.HasValue || endDate.Value.Date >= now.Date || endBehavior == TenancyEndBehaviorEnum.ContinueMonthToMonth;
        }

        private static string ResolveTenancyStatus(
            DateTimeOffset startDate,
            DateTimeOffset? endDate,
            TenancyEndBehaviorEnum endBehavior,
            DateTimeOffset? terminatedAt,
            DateTimeOffset now)
        {
            if (terminatedAt.HasValue) return "Terminated";
            if (startDate.Date > now.Date) return "Upcoming";
            if (endDate.HasValue && endDate.Value.Date < now.Date)
            {
                return endBehavior == TenancyEndBehaviorEnum.ContinueMonthToMonth ? "Month-to-month" : "Expired";
            }

            return "Active";
        }

        private sealed record PropertyScopeRow(int Id, string Name);
    }
}
