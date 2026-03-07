using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using Common.CommunicationModels;
using RentHub.API.Models.Entities;
using Common.Enums;
using System.Security.Claims;
using System;

namespace RentHub.API.Controllers
{
    /// <summary>
    /// Manages property manager assignments for properties.  Landlords can assign users as
    /// managers to their properties with read-only or read-write permissions.
    /// Managers can oversee all apartments within the property depending on permission level.
    /// </summary>
    [ApiController]
    [Route("api/properties/{propertyId}/managers")]
    public class PropertyManagersController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<ApplicationRole> _roleManager;

        public PropertyManagersController(ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            RoleManager<ApplicationRole> roleManager)
        {
            _context = context;
            _userManager = userManager;
            _roleManager = roleManager;
        }

        /// <summary>
        /// Lists all managers assigned to the specified property.  Accessible by the landlord,
        /// managers themselves and owners assigned to any apartment within the property.
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetManagers(int propertyId)
        {
            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                // Use FirstOrDefaultAsync to respect global query filters (and avoid retrieving deleted properties)
                var property = await _context.Properties.FirstOrDefaultAsync(p => p.Id == propertyId);
                if (property == null) return NotFound("Property not found.");
                // Access rules: landlord or manager of the property or owner of an apartment in the property
                bool hasAccess = false;
                if (property.LandlordId == userId)
                {
                    hasAccess = true;
                }
                else
                {
                    // manager assignments
                    hasAccess = await _context.PropertyManagerAssignments
                        .AnyAsync(m => m.PropertyId == propertyId && m.ManagerId == userId);
                    if (!hasAccess)
                    {
                        // owner of any apartment in the property
                        hasAccess = await _context.ApartmentOwners
                            .Include(o => o.Apartment)
                            .AnyAsync(o => o.Apartment!.PropertyId == propertyId && o.OwnerId == userId);
                    }
                }
                if (!hasAccess) return Forbid();
                var managers = await _context.PropertyManagerAssignments
                    .Include(m => m.Manager)
                    .Where(m => m.PropertyId == propertyId)
                    .Select(m => new PropertyManagerDto
                    {
                        Id = m.Id,
                        ManagerId = m.ManagerId,
                        ManagerName = m.Manager != null ? m.Manager.FullName ?? string.Empty : string.Empty,
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

        /// <summary>
        /// Assigns a user as manager of the specified property.  Only the landlord of the property can
        /// perform this action and must have an active, approved subscription.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> AddManager(int propertyId, [FromBody] AddManagerRequest request)
        {
            try
            {
                if (!ModelState.IsValid) return BadRequest(ModelState);
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                // Use FirstOrDefaultAsync to respect global query filters
                var property = await _context.Properties.FirstOrDefaultAsync(p => p.Id == propertyId);
                if (property == null) return NotFound("Property not found.");
                // Only landlord can add manager
                if (property.LandlordId != userId)
                {
                    return Forbid();
                }
                // Check landlord subscription approval
                var subscription = await _context.UserSubscriptions
                    .Where(us => us.UserId == userId && us.EndDate > DateTime.UtcNow && us.IsApproved)
                    .FirstOrDefaultAsync();
                if (subscription == null)
                {
                    return BadRequest("Your subscription is inactive or not approved. You cannot add managers.");
                }
                // Create or find manager user
                var managerUser = await _userManager.FindByEmailAsync(request.Email);
                if (managerUser == null)
                {
                    managerUser = new ApplicationUser
                    {
                        UserName = request.Email,
                        Email = request.Email,
                        FullName = request.FullName,
                        CountryCode = request.CountryCode
                    };
                    var tempPassword = Guid.NewGuid().ToString("N").Substring(0, 8) + "@!";
                    var createResult = await _userManager.CreateAsync(managerUser, tempPassword);
                    if (!createResult.Succeeded)
                    {
                        return BadRequest(createResult.Errors);
                    }
                }
                // Ensure the user has the Manager role
                if (!await _userManager.IsInRoleAsync(managerUser, "Manager"))
                {
                    await _userManager.AddToRoleAsync(managerUser, "Manager");
                }
                // Check if assignment already exists
                var existing = await _context.PropertyManagerAssignments
                    .FirstOrDefaultAsync(m => m.PropertyId == propertyId && m.ManagerId == managerUser.Id);
                if (existing != null)
                {
                    return BadRequest("This user is already a manager of the property.");
                }
                var assignment = new PropertyManagerAssignment
                {
                    PropertyId = propertyId,
                    ManagerId = managerUser.Id,
                    Permission = request.Permission,
                    // Audit: record creator
                    CreatedBy = userId,
                    CreatedAt = DateTime.UtcNow,
                    IsDeleted = false
                };
                _context.PropertyManagerAssignments.Add(assignment);
                await _context.SaveChangesAsync();
                // Map to DTO for return
                var dto = new PropertyManagerDto
                {
                    Id = assignment.Id,
                    ManagerId = assignment.ManagerId,
                    ManagerName = managerUser.FullName ?? string.Empty,
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

        /// <summary>
        /// Updates a manager's permission assignment.  Only the landlord may modify permissions.
        /// </summary>
        [HttpPut("{managerAssignmentId}")]
        [Authorize]
        public async Task<IActionResult> UpdateManagerPermission(int propertyId, int managerAssignmentId, [FromBody] PermissionLevelEnum permission)
        {
            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                var assignment = await _context.PropertyManagerAssignments
                    .Include(m => m.Property)
                    .FirstOrDefaultAsync(m => m.Id == managerAssignmentId && m.PropertyId == propertyId);
                if (assignment == null) return NotFound("Manager assignment not found.");
                var property = assignment.Property;
                if (property == null) return NotFound("Property not found.");
                if (property.LandlordId != userId)
                {
                    return Forbid();
                }
                assignment.Permission = permission;
                // Audit: record update details
                assignment.UpdatedBy = userId;
                assignment.UpdatedAt = DateTime.UtcNow;
                _context.PropertyManagerAssignments.Update(assignment);
                await _context.SaveChangesAsync();
                return Ok(new { Message = "Permission updated." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Removes a manager assignment from a property.  Only the landlord may remove managers.
        /// </summary>
        [HttpDelete("{managerAssignmentId}")]
        [Authorize]
        public async Task<IActionResult> RemoveManager(int propertyId, int managerAssignmentId)
        {
            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                var assignment = await _context.PropertyManagerAssignments
                    .Include(m => m.Property)
                    .FirstOrDefaultAsync(m => m.Id == managerAssignmentId && m.PropertyId == propertyId);
                if (assignment == null) return NotFound("Manager assignment not found.");
                var property = assignment.Property;
                if (property == null) return NotFound("Property not found.");
                if (property.LandlordId != userId)
                {
                    return Forbid();
                }
                // Soft delete
                assignment.IsDeleted = true;
                assignment.DeletedBy = userId;
                assignment.DeletedAt = DateTime.UtcNow;
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