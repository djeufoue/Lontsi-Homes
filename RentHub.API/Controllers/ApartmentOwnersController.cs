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
    /// Manages owner assignments for apartments.  Landlords can assign owners to specific
    /// apartments with read-only or read-write permissions.  Owners are not tenants; they
    /// simply manage the unit on behalf of the landlord.
    /// </summary>
    [ApiController]
    [Route("api/apartments/{apartmentId}/owners")]
    public class ApartmentOwnersController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<ApplicationRole> _roleManager;

        public ApartmentOwnersController(ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            RoleManager<ApplicationRole> roleManager)
        {
            _context = context;
            _userManager = userManager;
            _roleManager = roleManager;
        }

        /// <summary>
        /// Lists all owners assigned to the given apartment.  Only the landlord, managers or
        /// owners themselves may view this list.
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetOwners(int apartmentId)
        {
            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var apartment = await _context.Apartments
                    .Include(a => a.Property)
                    .FirstOrDefaultAsync(a => a.Id == apartmentId);
                if (apartment == null) return NotFound("Apartment not found.");

                // Check access: landlord, manager of the property, or owner of the apartment
                bool hasAccess = false;
                if (apartment.Property?.LandlordId == userId)
                {
                    hasAccess = true;
                }
                else
                {
                    // manager with read permission
                    hasAccess = await _context.PropertyManagerAssignments
                        .AnyAsync(m => m.PropertyId == apartment.PropertyId && m.ManagerId == userId);
                    if (!hasAccess)
                    {
                        // owner of apartment
                        hasAccess = await _context.ApartmentOwners
                            .AnyAsync(o => o.ApartmentId == apartmentId && o.OwnerId == userId);
                    }
                }
                if (!hasAccess) return Forbid();

                var owners = await _context.ApartmentOwners
                    .Include(o => o.Owner)
                    .Where(o => o.ApartmentId == apartmentId)
                    .Select(o => new ApartmentOwnerDto
                    {
                        Id = o.Id,
                        OwnerId = o.OwnerId,
                        OwnerName = o.Owner != null ? o.Owner.FullName ?? string.Empty : string.Empty,
                        Permission = o.Permission,
                        AssignedAt = o.CreatedAt
                    })
                    .ToListAsync();
                return Ok(owners);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Assigns a user as an owner of the specified apartment.  Only the landlord of the apartment's
        /// property can perform this action and must have an active, approved subscription.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> AddOwner(int apartmentId, [FromBody] AddOwnerRequest request)
        {
            try
            {
                if (!ModelState.IsValid) return BadRequest(ModelState);
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                var apartment = await _context.Apartments
                    .Include(a => a.Property)
                    .FirstOrDefaultAsync(a => a.Id == apartmentId);
                if (apartment == null) return NotFound("Apartment not found.");
                // Only landlord of the property can add owners
                if (apartment.Property?.LandlordId != userId)
                {
                    return Forbid();
                }
                // Check landlord subscription approval
                var subscription = await _context.UserSubscriptions
                    .Include(us => us.SubscriptionPlan)
                    .Where(us => us.UserId == userId && us.EndDate > DateTimeOffset.UtcNow && us.IsApproved)
                    .FirstOrDefaultAsync();
                if (subscription == null)
                {
                    return BadRequest("Your subscription is inactive or not approved. You cannot add owners.");
                }
                // Create or find owner user
                var ownerUser = await _userManager.FindByEmailAsync(request.Email);
                if (ownerUser == null)
                {
                    ownerUser = new ApplicationUser
                    {
                        UserName = request.Email,
                        Email = request.Email,
                        FullName = request.FullName,
                        CountryCode = request.CountryCode
                    };
                    // Generate a temporary password
                    var tempPassword = Guid.NewGuid().ToString("N").Substring(0, 8) + "@!";
                    var createResult = await _userManager.CreateAsync(ownerUser, tempPassword);
                    if (!createResult.Succeeded)
                    {
                        return BadRequest(createResult.Errors);
                    }
                }
                // Ensure the user has the Owner role
                if (!await _userManager.IsInRoleAsync(ownerUser, "Owner"))
                {
                    await _userManager.AddToRoleAsync(ownerUser, "Owner");
                }
                // Check if assignment already exists
                var existing = await _context.ApartmentOwners
                    .FirstOrDefaultAsync(o => o.ApartmentId == apartmentId && o.OwnerId == ownerUser.Id);
                if (existing != null)
                {
                    return BadRequest("This user is already an owner of the apartment.");
                }
                var assignment = new ApartmentOwner
                {
                    ApartmentId = apartmentId,
                    OwnerId = ownerUser.Id,
                    Permission = request.Permission,
                    // Audit: record who created the assignment
                    CreatedBy = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsDeleted = false
                };
                _context.ApartmentOwners.Add(assignment);
                await _context.SaveChangesAsync();
                // Map to DTO for return
                var dto = new ApartmentOwnerDto
                {
                    Id = assignment.Id,
                    OwnerId = assignment.OwnerId,
                    OwnerName = ownerUser.FullName ?? string.Empty,
                    Permission = assignment.Permission,
                    AssignedAt = assignment.CreatedAt
                };
                return CreatedAtAction(nameof(GetOwners), new { apartmentId }, dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Updates the permission of an existing owner assignment.  Only the landlord can modify permissions.
        /// </summary>
        [HttpPut("{ownerAssignmentId}")]
        [Authorize]
        public async Task<IActionResult> UpdateOwnerPermission(int apartmentId, int ownerAssignmentId, [FromBody] PermissionLevelEnum permission)
        {
            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                var assignment = await _context.ApartmentOwners
                    .Include(a => a.Apartment)
                    .FirstOrDefaultAsync(a => a.Id == ownerAssignmentId && a.ApartmentId == apartmentId);
                if (assignment == null) return NotFound("Owner assignment not found.");
                // Only landlord of the property can update
                var apartment = assignment.Apartment;
                if (apartment == null) return NotFound("Apartment not found.");
                if (apartment.Property?.LandlordId != userId)
                {
                    return Forbid();
                }
                assignment.Permission = permission;
                // Audit: record update details
                assignment.UpdatedBy = userId;
                assignment.UpdatedAt = DateTimeOffset.UtcNow;
                _context.ApartmentOwners.Update(assignment);
                await _context.SaveChangesAsync();
                return Ok(new { Message = "Permission updated." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Removes an owner assignment from an apartment.  Only the landlord may remove owners.
        /// </summary>
        [HttpDelete("{ownerAssignmentId}")]
        [Authorize]
        public async Task<IActionResult> RemoveOwner(int apartmentId, int ownerAssignmentId)
        {
            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                var assignment = await _context.ApartmentOwners
                    .Include(a => a.Apartment)
                    .FirstOrDefaultAsync(a => a.Id == ownerAssignmentId && a.ApartmentId == apartmentId);
                if (assignment == null) return NotFound("Owner assignment not found.");
                var apartment = assignment.Apartment;
                if (apartment == null) return NotFound("Apartment not found.");
                if (apartment.Property?.LandlordId != userId)
                {
                    return Forbid();
                }
                // Soft delete: mark as deleted and record who deleted
                assignment.IsDeleted = true;
                assignment.DeletedBy = userId;
                assignment.DeletedAt = DateTime.UtcNow;
                _context.ApartmentOwners.Update(assignment);
                await _context.SaveChangesAsync();
                return Ok(new { Message = "Owner removed." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }
    }
}