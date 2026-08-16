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

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/properties/{propertyId}/managers")]
    public class PropertyManagersController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IUserOnboardingService _userOnboardingService;

        public PropertyManagersController(ApplicationDbContext context, IUserOnboardingService userOnboardingService)
        {
            _context = context;
            _userOnboardingService = userOnboardingService;
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
                var hasAccess = await PropertyHelpers.CanAccessPropertyAsync(_context, propertyId, userId, isAdmin);
                if (!hasAccess) return Forbid();

                var managers = await _context.PropertyManagerAssignments
                    .Include(m => m.Manager)
                    .Where(m => m.PropertyId == propertyId)
                    .OrderByDescending(m => m.CreatedAt)
                    .Select(m => new PropertyManagerDto
                    {
                        Id = m.Id,
                        ManagerId = m.ManagerId,
                        ManagerName = m.Manager != null ? (m.Manager.FullName ?? m.Manager.Email ?? "") : "",
                        Permission = m.Permission,
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
                var canWrite = await PropertyHelpers.CanWritePropertyAsync(_context, propertyId, userId, isAdmin);
                if (!canWrite) return Forbid();

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

                var managerUser = (await _userOnboardingService.EnsureUserAsync(
                    request.Email,
                    request.FullName,
                    request.CountryCode,
                    request.PhoneNumber,
                    null,
                    "Manager")).User;

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
                    Permission = request.Permission,
                    CreatedBy = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsDeleted = false
                };

                _context.PropertyManagerAssignments.Add(assignment);
                await _context.SaveChangesAsync();

                var dto = new PropertyManagerDto
                {
                    Id = assignment.Id,
                    ManagerId = assignment.ManagerId,
                    ManagerName = managerUser.FullName ?? managerUser.Email ?? "",
                    Permission = assignment.Permission,
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
                var canWrite = await PropertyHelpers.CanWritePropertyAsync(_context, propertyId, userId, isAdmin);
                if (!canWrite) return Forbid();

                assignment.Permission = permission;
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
                var canWrite = await PropertyHelpers.CanWritePropertyAsync(_context, propertyId, userId, isAdmin);
                if (!canWrite) return Forbid();

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
    }
}




