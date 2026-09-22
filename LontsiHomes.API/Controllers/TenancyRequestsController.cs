using Common.CommunicationModels;
using Common.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LontsiHomes.API.Data;
using LontsiHomes.API.Helpers;
using LontsiHomes.API.Services.Permissions;

namespace LontsiHomes.API.Controllers
{
    [ApiController]
    [Route("api/tenancy-requests")]
    [Authorize]
    public class TenancyRequestsController : ControllerBase
    {
        private const int MaximumPageSize = 50;
        private readonly ApplicationDbContext _context;
        private readonly IManagerPermissionService _permissionService;

        public TenancyRequestsController(
            ApplicationDbContext context,
            IManagerPermissionService permissionService)
        {
            _context = context;
            _permissionService = permissionService;
        }

        [HttpGet("pending-count")]
        public async Task<IActionResult> GetPendingCount(CancellationToken cancellationToken = default)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            var showsDecisionResponses = User.IsInRole("Tenant") &&
                                         !User.IsInRole("Admin") &&
                                         !User.IsInRole("Landlord") &&
                                         !User.IsInRole("Manager") &&
                                         !User.IsInRole("Owner");

            var accessibleTenancyIds = await GetAccessibleTenancyIdsAsync(
                userId,
                User.IsInRole("Admin"),
                cancellationToken);
            if (accessibleTenancyIds.Count == 0)
            {
                return Ok(new TenancyRequestPendingCountDto { ShowsDecisionResponses = showsDecisionResponses });
            }

            if (showsDecisionResponses)
            {
                var renewalResponseCount = await _context.TenancyExtensionRequests
                    .AsNoTracking()
                    .CountAsync(request =>
                        !request.IsDeleted &&
                        accessibleTenancyIds.Contains(request.TenancyId) &&
                        request.RequestedById == userId &&
                        (request.Status == TenancyExtensionStatusEnum.Approved ||
                         request.Status == TenancyExtensionStatusEnum.Rejected) &&
                        !request.DecisionViewedAt.HasValue,
                        cancellationToken);
                var terminationResponseCount = await _context.TenancyTerminationRequests
                    .AsNoTracking()
                    .CountAsync(request =>
                        !request.IsDeleted &&
                        accessibleTenancyIds.Contains(request.TenancyId) &&
                        request.RequestedById == userId &&
                        (request.Status == TenancyTerminationRequestStatusEnum.Approved ||
                         request.Status == TenancyTerminationRequestStatusEnum.Rejected) &&
                        !request.DecisionViewedAt.HasValue,
                        cancellationToken);

                return Ok(new TenancyRequestPendingCountDto
                {
                    Count = renewalResponseCount + terminationResponseCount,
                    ShowsDecisionResponses = true
                });
            }

            var renewalCount = await _context.TenancyExtensionRequests
                .AsNoTracking()
                .CountAsync(request =>
                    !request.IsDeleted &&
                    accessibleTenancyIds.Contains(request.TenancyId) &&
                    request.Status == TenancyExtensionStatusEnum.Pending,
                    cancellationToken);
            var terminationCount = await _context.TenancyTerminationRequests
                .AsNoTracking()
                .CountAsync(request =>
                    !request.IsDeleted &&
                    accessibleTenancyIds.Contains(request.TenancyId) &&
                    request.Status == TenancyTerminationRequestStatusEnum.Pending,
                    cancellationToken);

            return Ok(new TenancyRequestPendingCountDto
            {
                Count = renewalCount + terminationCount,
                ShowsDecisionResponses = false
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetRequests(
            string? type = null,
            string? status = null,
            int? propertyId = null,
            int? apartmentId = null,
            int page = 1,
            int pageSize = 12,
            CancellationToken cancellationToken = default)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            var isTenantResponseInbox = User.IsInRole("Tenant") &&
                                        !User.IsInRole("Admin") &&
                                        !User.IsInRole("Landlord") &&
                                        !User.IsInRole("Manager") &&
                                        !User.IsInRole("Owner");

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, MaximumPageSize);
            var accessibleTenancyIds = await GetAccessibleTenancyIdsAsync(userId, User.IsInRole("Admin"), cancellationToken);
            if (isTenantResponseInbox && accessibleTenancyIds.Count > 0)
            {
                var viewedAt = DateTimeOffset.UtcNow;
                await _context.TenancyExtensionRequests
                    .Where(request =>
                        !request.IsDeleted &&
                        accessibleTenancyIds.Contains(request.TenancyId) &&
                        request.RequestedById == userId &&
                        (request.Status == TenancyExtensionStatusEnum.Approved ||
                         request.Status == TenancyExtensionStatusEnum.Rejected) &&
                        !request.DecisionViewedAt.HasValue)
                    .ExecuteUpdateAsync(update => update.SetProperty(request => request.DecisionViewedAt, viewedAt), cancellationToken);
                await _context.TenancyTerminationRequests
                    .Where(request =>
                        !request.IsDeleted &&
                        accessibleTenancyIds.Contains(request.TenancyId) &&
                        request.RequestedById == userId &&
                        (request.Status == TenancyTerminationRequestStatusEnum.Approved ||
                         request.Status == TenancyTerminationRequestStatusEnum.Rejected) &&
                        !request.DecisionViewedAt.HasValue)
                    .ExecuteUpdateAsync(update => update.SetProperty(request => request.DecisionViewedAt, viewedAt), cancellationToken);
            }
            var scopeRows = await _context.Tenancies
                .AsNoTracking()
                .Where(tenancy => accessibleTenancyIds.Contains(tenancy.Id))
                .Select(tenancy => new
                {
                    TenancyId = tenancy.Id,
                    tenancy.ApartmentId,
                    PropertyId = tenancy.Apartment!.PropertyId,
                    PropertyName = tenancy.Apartment.Property!.Name,
                    ApartmentName = tenancy.Apartment.Name,
                    tenancy.EndDate,
                    tenancy.TerminatedAt
                })
                .ToListAsync(cancellationToken);
            var propertyOptions = scopeRows
                .GroupBy(row => new { row.PropertyId, row.PropertyName })
                .Select(group => new TenancyRequestPropertyOptionDto
                {
                    Id = group.Key.PropertyId,
                    Label = group.Key.PropertyName
                })
                .OrderBy(option => option.Label)
                .ToList();
            var apartmentOptions = scopeRows
                .GroupBy(row => new { row.ApartmentId, row.PropertyId, row.ApartmentName })
                .Select(group => new TenancyRequestApartmentOptionDto
                {
                    Id = group.Key.ApartmentId,
                    PropertyId = group.Key.PropertyId,
                    Label = group.Key.ApartmentName
                })
                .OrderBy(option => option.Label)
                .ToList();
            var nowUtc = DateTimeOffset.UtcNow;
            var requestableTenancyIds = await _context.TenancyMembers
                .AsNoTracking()
                .Where(member =>
                    !member.IsDeleted &&
                    member.MemberId == userId &&
                    (member.Role == TenancyMemberRoleEnum.MainTenant || member.Role == TenancyMemberRoleEnum.CoTenant) &&
                    accessibleTenancyIds.Contains(member.TenancyId) &&
                    member.Tenancy != null &&
                    !member.Tenancy.IsDeleted &&
                    !member.Tenancy.TerminatedAt.HasValue &&
                    member.Tenancy.StartDate.Date <= nowUtc.Date &&
                    (!member.Tenancy.EndDate.HasValue ||
                     member.Tenancy.EndDate.Value.Date >= nowUtc.Date ||
                     member.Tenancy.EndBehavior == TenancyEndBehaviorEnum.ContinueMonthToMonth))
                .Select(member => member.TenancyId)
                .Distinct()
                .ToListAsync(cancellationToken);
            var requestableIdSet = requestableTenancyIds.ToHashSet();
            var requestableTenancies = scopeRows
                .Where(row => requestableIdSet.Contains(row.TenancyId) && !row.TerminatedAt.HasValue)
                .OrderBy(row => row.PropertyName)
                .ThenBy(row => row.ApartmentName)
                .Select(row => new TenancyRequestContextOptionDto
                {
                    TenancyId = row.TenancyId,
                    PropertyId = row.PropertyId,
                    ApartmentId = row.ApartmentId,
                    PropertyName = row.PropertyName,
                    ApartmentName = row.ApartmentName,
                    EndDate = row.EndDate
                })
                .ToList();

            var renewalEntities = await _context.TenancyExtensionRequests
                .AsNoTracking()
                .Include(request => request.RequestedBy)
                .Include(request => request.ApprovedBy)
                .Include(request => request.Tenancy)
                .ThenInclude(tenancy => tenancy!.Apartment)
                .ThenInclude(apartment => apartment!.Property)
                .Where(request => !request.IsDeleted && accessibleTenancyIds.Contains(request.TenancyId))
                .ToListAsync(cancellationToken);
            var renewals = renewalEntities.Select(request => new TenancyRequestItemDto
                {
                    Id = request.Id,
                    TenancyId = request.TenancyId,
                    ApartmentId = request.Tenancy!.ApartmentId,
                    PropertyId = request.Tenancy.Apartment!.PropertyId,
                    Type = TenancyRequestTypeEnum.Renewal,
                    Status = request.Status.ToString(),
                    PropertyName = request.Tenancy.Apartment.Property!.Name,
                    ApartmentName = request.Tenancy.Apartment.Name,
                    RequestedById = request.RequestedById,
                    RequestedByName = request.RequestedBy!.FullName ?? request.RequestedBy.Email ?? "Tenant",
                    RequestedAt = request.CreatedAt,
                    RequestedDate = request.ProposedEndDate,
                    PreviousEndDate = request.OriginalEndDate,
                    ReviewedByName = request.ApprovedBy == null ? null : request.ApprovedBy.FullName ?? request.ApprovedBy.Email,
                    ReviewedAt = request.ApprovedAt,
                    RejectionReason = request.RejectionReason
                }).ToList();

            var terminationEntities = await _context.TenancyTerminationRequests
                .AsNoTracking()
                .Include(request => request.RequestedBy)
                .Include(request => request.ReviewedBy)
                .Include(request => request.Tenancy)
                .ThenInclude(tenancy => tenancy!.Apartment)
                .ThenInclude(apartment => apartment!.Property)
                .Where(request => !request.IsDeleted && accessibleTenancyIds.Contains(request.TenancyId))
                .ToListAsync(cancellationToken);
            var terminations = terminationEntities.Select(request => new TenancyRequestItemDto
                {
                    Id = request.Id,
                    TenancyId = request.TenancyId,
                    ApartmentId = request.Tenancy!.ApartmentId,
                    PropertyId = request.Tenancy.Apartment!.PropertyId,
                    Type = TenancyRequestTypeEnum.Termination,
                    Status = request.Status.ToString(),
                    PropertyName = request.Tenancy.Apartment.Property!.Name,
                    ApartmentName = request.Tenancy.Apartment.Name,
                    RequestedById = request.RequestedById,
                    RequestedByName = request.RequestedBy!.FullName ?? request.RequestedBy.Email ?? "Tenant",
                    RequestedAt = request.CreatedAt,
                    RequestedDate = request.RequestedEndDate,
                    PreviousEndDate = request.OriginalEndDate,
                    RequestReason = request.Reason,
                    ReviewedByName = request.ReviewedBy == null ? null : request.ReviewedBy.FullName ?? request.ReviewedBy.Email,
                    ReviewedAt = request.ReviewedAt,
                    RejectionReason = request.RejectionReason,
                    IsDirectDecision = string.Equals(request.RequestedById, request.ReviewedById, StringComparison.Ordinal)
                }).ToList();

            IEnumerable<TenancyRequestItemDto> filtered = renewals.Concat(terminations);
            if (propertyId.HasValue)
            {
                filtered = filtered.Where(request => request.PropertyId == propertyId.Value);
            }
            if (apartmentId.HasValue)
            {
                filtered = filtered.Where(request => request.ApartmentId == apartmentId.Value);
            }
            if (Enum.TryParse<TenancyRequestTypeEnum>(type, true, out var parsedType))
            {
                filtered = filtered.Where(request => request.Type == parsedType);
            }
            if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "All", StringComparison.OrdinalIgnoreCase))
            {
                filtered = filtered.Where(request => string.Equals(request.Status, status, StringComparison.OrdinalIgnoreCase));
            }

            var ordered = filtered.OrderByDescending(request => request.RequestedAt).ThenByDescending(request => request.Id).ToList();
            var totalItems = ordered.Count;
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));
            page = Math.Min(page, totalPages);
            var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            foreach (var item in items)
            {
                var permission = item.Type == TenancyRequestTypeEnum.Renewal
                    ? ManagerPermission.RenewTenancy
                    : ManagerPermission.TerminateTenancy;
                item.CanReview = string.Equals(item.Status, "Pending", StringComparison.OrdinalIgnoreCase) &&
                                 await _permissionService.HasTenancyPermissionAsync(
                                     userId,
                                     item.TenancyId,
                                     permission,
                                     User.IsInRole("Admin"));
                item.CanWithdraw = string.Equals(item.Status, "Pending", StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(item.RequestedById, userId, StringComparison.Ordinal);
                item.CanCancel = item.Type == TenancyRequestTypeEnum.Termination &&
                                 string.Equals(item.Status, TenancyTerminationRequestStatusEnum.Approved.ToString(), StringComparison.OrdinalIgnoreCase) &&
                                 await _permissionService.HasTenancyPermissionAsync(
                                     userId,
                                     item.TenancyId,
                                     ManagerPermission.TerminateTenancy,
                                     User.IsInRole("Admin"));
            }

            return Ok(new TenancyRequestListDto
            {
                Items = items,
                Properties = propertyOptions,
                Apartments = apartmentOptions,
                RequestableTenancies = requestableTenancies,
                PropertyId = propertyId,
                ApartmentId = apartmentId,
                Page = page,
                PageSize = pageSize,
                TotalItems = totalItems,
                TotalPages = totalPages
            });
        }

        private async Task<HashSet<int>> GetAccessibleTenancyIdsAsync(
            string userId,
            bool isAdmin,
            CancellationToken cancellationToken)
        {
            if (isAdmin)
            {
                return (await _context.Tenancies.AsNoTracking().Select(tenancy => tenancy.Id).ToListAsync(cancellationToken)).ToHashSet();
            }

            var apartmentIds = new HashSet<int>();
            apartmentIds.UnionWith(await _context.Apartments
                .AsNoTracking()
                .Where(apartment => apartment.Property!.LandlordId == userId)
                .Select(apartment => apartment.Id)
                .ToListAsync(cancellationToken));
            apartmentIds.UnionWith(await _context.ApartmentOwners
                .AsNoTracking()
                .Where(owner => !owner.IsDeleted && owner.OwnerId == userId)
                .Select(owner => owner.ApartmentId)
                .ToListAsync(cancellationToken));

            var assignments = await _context.PropertyManagerAssignments
                .AsNoTracking()
                .Include(assignment => assignment.ApartmentOverrides)
                .Where(assignment => !assignment.IsDeleted && assignment.ManagerId == userId)
                .ToListAsync(cancellationToken);
            var propertyIds = assignments.Select(assignment => assignment.PropertyId).Distinct().ToList();
            var propertyApartments = await _context.Apartments
                .AsNoTracking()
                .Where(apartment => propertyIds.Contains(apartment.PropertyId))
                .Select(apartment => new { apartment.Id, apartment.PropertyId })
                .ToListAsync(cancellationToken);

            foreach (var assignment in assignments)
            {
                foreach (var apartment in propertyApartments.Where(apartment => apartment.PropertyId == assignment.PropertyId))
                {
                    var apartmentOverride = assignment.ApartmentOverrides.FirstOrDefault(item => item.ApartmentId == apartment.Id);
                    var hasAccess = apartmentOverride?.HasAccess ?? assignment.AccessAllApartments;
                    var flags = assignment.PermissionFlags | (apartmentOverride?.AllowedPermissionFlags ?? 0L);
                    flags &= ~(apartmentOverride?.DeniedPermissionFlags ?? 0L);
                    if (hasAccess && (flags & (long)ManagerPermission.ViewTenancies) != 0)
                    {
                        apartmentIds.Add(apartment.Id);
                    }
                }
            }

            var tenancyIds = await _context.Tenancies
                .AsNoTracking()
                .Where(tenancy => apartmentIds.Contains(tenancy.ApartmentId))
                .Select(tenancy => tenancy.Id)
                .ToListAsync(cancellationToken);
            tenancyIds.AddRange(await _context.TenancyMembers
                .AsNoTracking()
                .Where(member => !member.IsDeleted && member.MemberId == userId)
                .Select(member => member.TenancyId)
                .ToListAsync(cancellationToken));
            return tenancyIds.ToHashSet();
        }
    }
}
