using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using Common.CommunicationModels;
using RentHub.API.Models.Entities;
using RentHub.API.Helpers;
using Common.Enums;
using RentHub.API.Services.Users;
using RentHub.API.Services.Email;
using RentHub.API.Services.Permissions;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/properties/{propertyId}/managers")]
    public class PropertyManagersController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserOnboardingService _userOnboardingService;
        private readonly IManagerInvitationEmailService _managerInvitationEmailService;
        private readonly IManagerPermissionService _permissionService;

        public PropertyManagersController(
            ApplicationDbContext context,
            IUserOnboardingService userOnboardingService,
            IManagerInvitationEmailService managerInvitationEmailService,
            IManagerPermissionService permissionService)
        {
            _context = context;
            _userOnboardingService = userOnboardingService;
            _managerInvitationEmailService = managerInvitationEmailService;
            _permissionService = permissionService;
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetManagers(int propertyId)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var property = await _context.Properties.FirstOrDefaultAsync(p => p.Id == propertyId);
                if (property == null) return NotFound("Property not found.");

                var isAdmin = User.IsInRole("Admin");
                var canViewManagers = await _permissionService.HasPropertyPermissionAsync(
                    userId,
                    propertyId,
                    ManagerPermission.ViewManagers,
                    isAdmin);
                if (!canViewManagers) return Forbid();

                var managers = await _context.PropertyManagerAssignments
                    .Include(m => m.Manager)
                    .Where(m => m.PropertyId == propertyId)
                    .OrderByDescending(m => m.CreatedAt)
                    .Select(m => new PropertyManagerDto
                    {
                        Id = m.Id,
                        ManagerId = m.ManagerId,
                        ManagerName = m.Manager != null ? (m.Manager.FullName ?? m.Manager.Email ?? "") : "",
                        ManagerEmail = m.Manager != null ? (m.Manager.Email ?? "") : "",
                        Permission = m.Permission,
                        PermissionFlags = m.PermissionFlags,
                        AccessAllApartments = m.AccessAllApartments,
                        AssignedAt = m.CreatedAt
                    })
                    .ToListAsync();

                return Ok(managers);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> AddManager(int propertyId, [FromBody] AddManagerRequest request)
        {
            try
            {
                if (!ModelState.IsValid) return BadRequest(ModelState);

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var property = await _context.Properties.FirstOrDefaultAsync(p => p.Id == propertyId);
                if (property == null) return NotFound("Property not found.");

                var isAdmin = User.IsInRole("Admin");
                if (!await _permissionService.CanManageManagersAsync(userId, propertyId, isAdmin)) return Forbid();

                // Non-admin users must respect landlord subscription status.
                if (!isAdmin)
                {
                    var hasApprovedSubscription = await PaymentAvailabilityHelper.HasActiveSubscriptionAsync(_context, property.LandlordId);

                    if (!hasApprovedSubscription)
                    {
                        return StatusCode(StatusCodes.Status402PaymentRequired, new
                        {
                            Code = "SUBSCRIPTION_PAYMENT_REQUIRED",
                            Message = PaymentAvailabilityHelper.SubscriptionRequiredMessage
                        });
                    }
                }

                var invitedUser = await _userOnboardingService.EnsureUserAsync(
                    request.Email,
                    request.FullName,
                    request.CountryCode,
                    request.PhoneNumber,
                    null,
                    "Manager",
                    sendActivationEmail: false);
                var managerUser = invitedUser.User;

                var existing = await _context.PropertyManagerAssignments
                    .FirstOrDefaultAsync(m => m.PropertyId == propertyId && m.ManagerId == managerUser.Id && !m.IsDeleted);

                if (existing != null)
                    return BadRequest("This user is already a manager of the property.");

                var isApartmentMember = await _context.ApartmentOwners
                    .AnyAsync(o => !o.IsDeleted && o.OwnerId == managerUser.Id && o.Apartment != null && o.Apartment.PropertyId == propertyId);

                if (isApartmentMember)
                    return BadRequest("Apartment members cannot also be assigned as property members within the same property.");

                var isTenancyMember = await _context.TenancyMembers
                    .AnyAsync(m => !m.IsDeleted && m.MemberId == managerUser.Id && m.Tenancy != null && !m.Tenancy.IsDeleted && m.Tenancy.Apartment != null && m.Tenancy.Apartment.PropertyId == propertyId);

                if (isTenancyMember)
                    return BadRequest("Tenancy members cannot also be assigned as property members within the same property.");

                var assignment = new PropertyManagerAssignment
                {
                    PropertyId = propertyId,
                    ManagerId = managerUser.Id,
                    Permission = PermissionLevelEnum.ReadWrite,
                    PermissionFlags = (long)ManagerPermissionDefaults.Standard,
                    AccessAllApartments = true,
                    CreatedBy = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsDeleted = false
                };

                _context.PropertyManagerAssignments.Add(assignment);
                await _context.SaveChangesAsync();
                await _managerInvitationEmailService.SendPropertyAccessEmailAsync(managerUser, property, invitedUser.IsNewUser);

                var dto = new PropertyManagerDto
                {
                    Id = assignment.Id,
                    ManagerId = assignment.ManagerId,
                    ManagerName = managerUser.FullName ?? managerUser.Email ?? "",
                    ManagerEmail = managerUser.Email ?? string.Empty,
                    Permission = assignment.Permission,
                    PermissionFlags = assignment.PermissionFlags,
                    AccessAllApartments = assignment.AccessAllApartments,
                    AssignedAt = assignment.CreatedAt
                };

                return CreatedAtAction(nameof(GetManagers), new { propertyId }, dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPut("{managerAssignmentId}")]
        [Authorize]
        public async Task<IActionResult> UpdateManagerPermission(int propertyId, int managerAssignmentId, [FromBody] PermissionLevelEnum permission)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var assignment = await _context.PropertyManagerAssignments
                    .Include(m => m.Property)
                    .FirstOrDefaultAsync(m => m.Id == managerAssignmentId && m.PropertyId == propertyId);

                if (assignment == null) return NotFound("Manager assignment not found.");
                if (assignment.Property == null) return NotFound("Property not found.");

                var isAdmin = User.IsInRole("Admin");
                if (!await _permissionService.CanManageManagersAsync(userId, propertyId, isAdmin)) return Forbid();

                assignment.Permission = permission;
                assignment.PermissionFlags = permission == PermissionLevelEnum.ReadWrite
                    ? (long)ManagerPermissionDefaults.All
                    : (long)ManagerPermissionDefaults.ReadOnly;
                assignment.UpdatedBy = userId;
                assignment.UpdatedAt = DateTimeOffset.UtcNow;

                _context.PropertyManagerAssignments.Update(assignment);
                await _context.SaveChangesAsync();

                return Ok(new { Message = "Permission updated." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpDelete("{managerAssignmentId}")]
        [Authorize]
        public async Task<IActionResult> RemoveManager(int propertyId, int managerAssignmentId)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var assignment = await _context.PropertyManagerAssignments
                    .Include(m => m.Property)
                    .FirstOrDefaultAsync(m => m.Id == managerAssignmentId && m.PropertyId == propertyId);

                if (assignment == null) return NotFound("Manager assignment not found.");
                if (assignment.Property == null) return NotFound("Property not found.");

                var isAdmin = User.IsInRole("Admin");
                if (!await _permissionService.CanManageManagersAsync(userId, propertyId, isAdmin)) return Forbid();

                assignment.IsDeleted = true;
                assignment.DeletedBy = userId;
                assignment.DeletedAt = DateTimeOffset.UtcNow;

                _context.PropertyManagerAssignments.Update(assignment);
                await _context.SaveChangesAsync();

                return Ok(new { Message = "Manager removed." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpGet("{managerAssignmentId}/permissions")]
        [Authorize]
        public async Task<IActionResult> GetPermissionSettings(int propertyId, int managerAssignmentId)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
            if (!await _permissionService.CanManageManagersAsync(userId, propertyId, User.IsInRole("Admin"))) return Forbid();

            var assignment = await _context.PropertyManagerAssignments
                .AsNoTracking()
                .Include(item => item.Property)
                .Include(item => item.Manager)
                .Include(item => item.ApartmentOverrides)
                .FirstOrDefaultAsync(item => item.Id == managerAssignmentId && item.PropertyId == propertyId);
            if (assignment?.Property == null) return NotFound("Manager assignment not found.");

            var apartments = await _context.Apartments
                .AsNoTracking()
                .Where(item => item.PropertyId == propertyId)
                .OrderBy(item => item.Name)
                .Select(item => new { item.Id, item.Name })
                .ToListAsync();

            var overrideMap = assignment.ApartmentOverrides.ToDictionary(item => item.ApartmentId);
            return Ok(new ManagerPermissionSettingsDto
            {
                AssignmentId = assignment.Id,
                PropertyId = propertyId,
                PropertyName = assignment.Property.Name,
                ManagerId = assignment.ManagerId,
                ManagerName = assignment.Manager?.FullName ?? assignment.Manager?.Email ?? string.Empty,
                ManagerEmail = assignment.Manager?.Email ?? string.Empty,
                PermissionFlags = assignment.PermissionFlags,
                AccessAllApartments = assignment.AccessAllApartments,
                Apartments = apartments.Select(apartment =>
                {
                    overrideMap.TryGetValue(apartment.Id, out var value);
                    return new ManagerApartmentPermissionDto
                    {
                        ApartmentId = apartment.Id,
                        ApartmentName = apartment.Name,
                        HasAccess = value?.HasAccess ?? assignment.AccessAllApartments,
                        AllowedPermissionFlags = value?.AllowedPermissionFlags ?? 0,
                        DeniedPermissionFlags = value?.DeniedPermissionFlags ?? 0
                    };
                }).ToList()
            });
        }

        [HttpPut("{managerAssignmentId}/permissions")]
        [Authorize]
        public async Task<IActionResult> UpdatePermissionSettings(
            int propertyId,
            int managerAssignmentId,
            [FromBody] UpdateManagerPermissionSettingsRequest request)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
            if (!await _permissionService.CanManageManagersAsync(userId, propertyId, User.IsInRole("Admin"))) return Forbid();

            var allFlags = (long)ManagerPermissionDefaults.All;
            if ((request.PermissionFlags & ~allFlags) != 0 || request.Apartments.Any(item =>
                    (item.AllowedPermissionFlags & ~allFlags) != 0 ||
                    (item.DeniedPermissionFlags & ~allFlags) != 0 ||
                    (item.AllowedPermissionFlags & item.DeniedPermissionFlags) != 0))
            {
                return BadRequest("The permission payload contains invalid or conflicting values.");
            }

            var assignment = await _context.PropertyManagerAssignments
                .Include(item => item.ApartmentOverrides)
                .FirstOrDefaultAsync(item => item.Id == managerAssignmentId && item.PropertyId == propertyId);
            if (assignment == null) return NotFound("Manager assignment not found.");

            var validApartmentIds = await _context.Apartments
                .Where(item => item.PropertyId == propertyId)
                .Select(item => item.Id)
                .ToHashSetAsync();
            if (request.Apartments.Any(item => !validApartmentIds.Contains(item.ApartmentId)) ||
                request.Apartments.Select(item => item.ApartmentId).Distinct().Count() != request.Apartments.Count)
            {
                return BadRequest("Every apartment permission must belong to this property and appear only once.");
            }

            var previousFlags = assignment.PermissionFlags;
            var previousAllApartments = assignment.AccessAllApartments;
            assignment.PermissionFlags = request.PermissionFlags;
            assignment.AccessAllApartments = request.AccessAllApartments;
            assignment.Permission = (request.PermissionFlags & ~(long)ManagerPermissionDefaults.ReadOnly) != 0
                ? PermissionLevelEnum.ReadWrite
                : PermissionLevelEnum.ReadOnly;
            assignment.UpdatedBy = userId;
            assignment.UpdatedAt = DateTimeOffset.UtcNow;

            var requestedMap = request.Apartments.ToDictionary(item => item.ApartmentId);
            foreach (var existing in assignment.ApartmentOverrides.ToList())
            {
                if (!requestedMap.TryGetValue(existing.ApartmentId, out var requested) ||
                    (requested.HasAccess == request.AccessAllApartments && requested.AllowedPermissionFlags == 0 && requested.DeniedPermissionFlags == 0))
                {
                    _context.ManagerApartmentPermissionOverrides.Remove(existing);
                    continue;
                }

                existing.HasAccess = requested.HasAccess;
                existing.AllowedPermissionFlags = requested.AllowedPermissionFlags;
                existing.DeniedPermissionFlags = requested.DeniedPermissionFlags;
                existing.UpdatedBy = userId;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                requestedMap.Remove(existing.ApartmentId);
            }

            foreach (var requested in requestedMap.Values.Where(item =>
                         item.HasAccess != request.AccessAllApartments ||
                         item.AllowedPermissionFlags != 0 ||
                         item.DeniedPermissionFlags != 0))
            {
                assignment.ApartmentOverrides.Add(new ManagerApartmentPermissionOverride
                {
                    ApartmentId = requested.ApartmentId,
                    HasAccess = requested.HasAccess,
                    AllowedPermissionFlags = requested.AllowedPermissionFlags,
                    DeniedPermissionFlags = requested.DeniedPermissionFlags,
                    CreatedBy = userId,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }

            _context.ManagerPermissionAuditLogs.Add(new ManagerPermissionAuditLog
            {
                PropertyId = propertyId,
                PropertyManagerAssignmentId = assignment.Id,
                ManagerId = assignment.ManagerId,
                ChangedBy = userId,
                PreviousPermissionFlags = previousFlags,
                NewPermissionFlags = request.PermissionFlags,
                PreviousAccessAllApartments = previousAllApartments,
                NewAccessAllApartments = request.AccessAllApartments,
                Details = $"Apartment overrides submitted: {request.Apartments.Count}"
            });

            await _context.SaveChangesAsync();
            return Ok(new { Message = "Manager permissions updated." });
        }
    }
}




