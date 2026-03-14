using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Models.Entities;
using Common.Enums;
using Common.CommunicationModels;
using System.Security.Claims;

using RentHub.API.Helpers;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TenanciesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        public TenanciesController(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        /// <summary>
        /// Lists all tenancies for the current user.  Landlords see tenancies for their properties,
        /// tenants see their own tenancies, owners see tenancies for apartments they manage, and
        /// property managers see tenancies for properties they manage.
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetTenancies()
        {
            try
            {
                var userIdClaim = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userIdClaim))
                {
                    return Unauthorized();
                }
                // Tenancies associated with the user in any capacity: landlord, tenant, member, owner or manager
                var tenancies = await _context.Tenancies
                    .Include(t => t.Apartment)
                    .ThenInclude(a => a.Property)
                    .Include(t => t.Members)
                    .Where(t =>
                        // Tenant (primary tenant or additional member)
                        t.TenantId == userIdClaim ||
                        t.Members.Any(m => m.MemberId == userIdClaim) ||
                        // Landlord
                        t.Apartment!.Property!.LandlordId == userIdClaim ||
                        // Owner assigned to this apartment
                        _context.ApartmentOwners.Any(o => o.ApartmentId == t.ApartmentId && o.OwnerId == userIdClaim) ||
                        // Manager assigned to the property
                        _context.PropertyManagerAssignments.Any(m => m.PropertyId == t.Apartment.PropertyId && m.ManagerId == userIdClaim)
                    )
                    .Select(t => new TenancyDto
                    {
                        Id = t.Id,
                        ApartmentName = t.Apartment!.Name,
                        PropertyName = t.Apartment.Property!.Name,
                        StartDate = t.StartDate,
                        EndDate = t.EndDate,
                        MonthlyRent = t.MonthlyRent,
                        IsOwner = t.Apartment.Property.LandlordId == userIdClaim
                    })
                    .ToListAsync();
                return Ok(tenancies);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Creates a new tenancy under an apartment. Only the landlord, a manager with write permission,
        /// or an owner with write permission may create a tenancy.
        /// Dates are DateTimeOffset.
        /// Ensures no other tenancy overlaps in the same apartment for the requested period.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> CreateTenancy([FromBody] CreateTenancyRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                var apartment = await _context.Apartments
                    .Include(a => a.Property)
                    .FirstOrDefaultAsync(a => a.Id == request.ApartmentId);

                if (apartment == null)
                    return NotFound("Apartment not found.");

                if (apartment.Property == null)
                    return NotFound("Property not found.");

                // Permission: landlord, manager RW on property, or owner RW on apartment
                bool canWrite =
                    apartment.Property.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m =>
                        m.PropertyId == apartment.PropertyId &&
                        m.ManagerId == userId &&
                        m.Permission == PermissionLevelEnum.ReadWrite) ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        o.ApartmentId == request.ApartmentId &&
                        o.OwnerId == userId &&
                        o.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite)
                    return Forbid();

                // Validations
                if (request.EndDate.HasValue && request.EndDate.Value < request.StartDate)
                    return BadRequest("EndDate cannot be earlier than StartDate.");

                if (request.MaxMembers <= 0)
                    return BadRequest("MaxMembers must be greater than 0.");

                // ---- Overlap check (no clashing leases for the same apartment)
                // We treat null EndDate as "open-ended".
                var newStart = request.StartDate;
                var newEnd = request.EndDate;

                // Overlap rule:
                // existing.Start <= newEnd (or newEnd is null => always true)
                // AND newStart <= existing.End (or existing.End is null => always true)
                var hasOverlap = await _context.Tenancies
                    .Where(t => !t.IsDeleted && t.ApartmentId == request.ApartmentId)
                    .AnyAsync(t =>
                        (newEnd == null || t.StartDate <= newEnd.Value) &&
                        (t.EndDate == null || newStart <= t.EndDate.Value)
                    );

                if (hasOverlap)
                    return BadRequest("This apartment already has a tenancy that overlaps with the selected period.");

                // Create tenancy (TenantId is optional now; keep null)
                var tenancy = new Tenancy
                {
                    ApartmentId = request.ApartmentId,

                    StartDate = request.StartDate,
                    EndDate = request.EndDate,
                    MonthlyRent = request.MonthlyRent,
                    MaxMembers = request.MaxMembers,

                    CreatedBy = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsDeleted = false
                };

                _context.Tenancies.Add(tenancy);
                await _context.SaveChangesAsync();

                var dto = new TenancyDto
                {
                    Id = tenancy.Id,
                    ApartmentName = apartment.Name,
                    PropertyName = apartment.Property.Name,
                    StartDate = tenancy.StartDate,
                    EndDate = tenancy.EndDate,
                    MonthlyRent = tenancy.MonthlyRent,
                    IsOwner = apartment.Property.LandlordId == userId
                };

                return Ok(dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Updates the maximum number of members allowed for a tenancy.  Only the landlord of the
        /// apartment or an authorized manager/owner with write permission may modify this value.
        /// </summary>
        [HttpPut("{tenancyId}/max-members")]
        [Authorize]
        public async Task<IActionResult> UpdateMaxTenancyMembers(int tenancyId, [FromBody] int maxMembers)
        {
            try
            {
                if (maxMembers <= 0)
                {
                    return BadRequest("MaxMembers must be a positive integer.");
                }
                var tenancy = await _context.Tenancies
                    .Include(t => t.Apartment)
                    .ThenInclude(a => a.Property)
                    .FirstOrDefaultAsync(t => t.Id == tenancyId);

                if (tenancy == null) return NotFound("Tenancy not found.");

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                // Determine if user is landlord of the apartment
                bool isLandlord = tenancy.Apartment!.Property!.LandlordId == userId;
                bool canWrite = false;
                if (isLandlord)
                {
                    canWrite = true;
                }
                else
                {
                    // Check owner write permission
                    var ownerWrite = await _context.ApartmentOwners
                        .AnyAsync(o => o.ApartmentId == tenancy.ApartmentId && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite);
                    // Check manager write permission
                    var managerWrite = await _context.PropertyManagerAssignments
                        .AnyAsync(m => m.PropertyId == tenancy.Apartment.PropertyId && m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite);
                    canWrite = ownerWrite || managerWrite;
                }
                if (!canWrite)
                {
                    return Forbid();
                }
                tenancy.MaxMembers = maxMembers;
                tenancy.UpdatedBy = userId;
                tenancy.UpdatedAt = DateTime.UtcNow;
                _context.Tenancies.Update(tenancy);
                await _context.SaveChangesAsync();
                return Ok(new { Message = "MaxMembers updated successfully.", TenancyId = tenancy.Id, MaxMembers = tenancy.MaxMembers });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Extends or renews a tenancy by setting a new end date.  Only the landlord or
        /// an authorized manager/owner with write permission may perform this operation.
        /// </summary>
        [HttpPut("{id}/extend")]
        [Authorize]
        public async Task<IActionResult> ExtendTenancy(int id, [FromBody] ExtendTenancyRequest request)
        {
            try
            {
                var tenancy = await _context.Tenancies
                    .Include(t => t.Apartment!.Property)
                    .FirstOrDefaultAsync(t => t.Id == id);
                if (tenancy == null) return NotFound("Tenancy not found.");
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                // Validate new end date
                if (request.NewEndDate <= tenancy.StartDate)
                {
                    return BadRequest("New end date must be after the tenancy start date.");
                }
                // Determine if user can modify end date
                bool isLandlord = tenancy.Apartment!.Property!.LandlordId == userId;
                bool canWrite = false;
                if (isLandlord)
                {
                    canWrite = true;
                }
                else
                {
                    var ownerWrite = await _context.ApartmentOwners
                        .AnyAsync(o => o.ApartmentId == tenancy.ApartmentId && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite);
                    var managerWrite = await _context.PropertyManagerAssignments
                        .AnyAsync(m => m.PropertyId == tenancy!.Apartment.PropertyId && m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite);
                    canWrite = ownerWrite || managerWrite;
                }
                if (!canWrite)
                {
                    return Forbid();
                }
                tenancy.EndDate = request.NewEndDate;
                tenancy.UpdatedBy = userId;
                tenancy.UpdatedAt = DateTime.UtcNow;
                _context.Tenancies.Update(tenancy);
                await _context.SaveChangesAsync();
                return Ok(new { Message = "Tenancy extended successfully.", TenancyId = tenancy.Id, NewEndDate = tenancy.EndDate });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpGet("by-apartment/{apartmentId}")]
        [Authorize]
        public async Task<IActionResult> GetTenanciesByApartment(int apartmentId)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var apt = await _context.Apartments
                    .Include(a => a.Property)
                    .FirstOrDefaultAsync(a => a.Id == apartmentId);

                if (apt == null) return NotFound("Apartment not found.");

                // Access: landlord, manager of property, owner of apartment, or tenant of any tenancy in apt
                bool hasAccess =
                    apt.Property?.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m => m.PropertyId == apt.PropertyId && m.ManagerId == userId) ||
                    await _context.ApartmentOwners.AnyAsync(o => o.ApartmentId == apartmentId && o.OwnerId == userId) ||
                    await _context.Tenancies.AnyAsync(t => t.ApartmentId == apartmentId && t.TenantId == userId);

                if (!hasAccess) return Forbid();

                var list = await _context.Tenancies
                    .Where(t => t.ApartmentId == apartmentId && !t.IsDeleted)
                    .OrderByDescending(t => t.StartDate)
                    .Select(t => new TenancyDto
                    {
                        Id = t.Id,
                        ApartmentName = apt.Name,
                        PropertyName = apt.Property!.Name,
                        StartDate = t.StartDate,
                        EndDate = t.EndDate,
                        MonthlyRent = t.MonthlyRent,
                        IsOwner = apt.Property!.LandlordId == userId
                    })
                    .ToListAsync();

                return Ok(list);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Adds a member to a tenancy. Only landlord / manager RW / owner RW can add members.
        /// Will create the user if not exists (optional behavior), then add to tenancy.
        /// </summary>
        [HttpPost("{tenancyId}/members")]
        [Authorize]
        public async Task<IActionResult> AddTenancyMember(int tenancyId, [FromBody] AddTenancyMemberRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                var tenancy = await _context.Tenancies
                    .Include(t => t.Apartment)
                    .ThenInclude(a => a.Property)
                    .Include(t => t.Members)
                    .FirstOrDefaultAsync(t => t.Id == tenancyId);

                if (tenancy == null) return NotFound("Tenancy not found.");
                if (tenancy.Apartment?.Property == null) return NotFound("Property not found.");

                // permission: landlord OR manager RW OR owner RW
                bool canWrite =
                    tenancy.Apartment.Property.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m =>
                        m.PropertyId == tenancy.Apartment.PropertyId &&
                        m.ManagerId == userId &&
                        m.Permission == PermissionLevelEnum.ReadWrite) ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        o.ApartmentId == tenancy.ApartmentId &&
                        o.OwnerId == userId &&
                        o.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite) return Forbid();

                // Max members check (current members + optional primary tenant)
                var currentCount = tenancy.Members.Count(m => !m.IsDeleted);
                if (!string.IsNullOrEmpty(tenancy.TenantId)) currentCount += 1;

                if (currentCount >= tenancy.MaxMembers)
                    return BadRequest($"Maximum number of members ({tenancy.MaxMembers}) reached for this tenancy.");

                // Find or create user
                var email = (request.Email ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(email)) return BadRequest("Email is required.");

                var memberUser = await _userManager.FindByEmailAsync(email);
                if (memberUser == null)
                {
                    memberUser = new ApplicationUser
                    {
                        UserName = email,
                        Email = email,
                        FullName = request.FullName ?? string.Empty,
                        CountryCode = request.CountryCode ?? string.Empty
                    };

                    var tempPassword = Guid.NewGuid().ToString("N") + "aA!1";
                    var createRes = await _userManager.CreateAsync(memberUser, tempPassword);
                    if (!createRes.Succeeded) return BadRequest(createRes.Errors);

                    // if you have a role for tenancy member/tenant, assign it
                    await _userManager.AddToRoleAsync(memberUser, "Tenant");
                }

                // Prevent duplicates
                var alreadyMember = tenancy.Members.Any(m => !m.IsDeleted && m.MemberId == memberUser.Id);
                if (alreadyMember) return BadRequest("User is already a member of this tenancy.");

                // Add member entity
                var member = new TenancyMember
                {
                    TenancyId = tenancyId,
                    MemberId = memberUser.Id,
                    CreatedBy = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsDeleted = false
                };

                _context.TenancyMembers.Add(member);
                await _context.SaveChangesAsync();

                var dto = new TenancyMemberDto
                {
                    Id = member.Id,
                    TenancyId = member.TenancyId,
                    MemberId = member.MemberId,
                    FullName = memberUser.FullName ?? string.Empty,
                    CreatedAt = member.CreatedAt
                };

                return Ok(dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Removes a tenancy member (soft delete). Only landlord / manager RW / owner RW.
        /// </summary>
        [HttpDelete("{tenancyId}/members/{memberId}")]
        [Authorize]
        public async Task<IActionResult> RemoveTenancyMember(int tenancyId, int memberId)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                var tenancy = await _context.Tenancies
                    .Include(t => t.Apartment)
                    .ThenInclude(a => a.Property)
                    .FirstOrDefaultAsync(t => t.Id == tenancyId);

                if (tenancy == null) return NotFound("Tenancy not found.");
                if (tenancy.Apartment?.Property == null) return NotFound("Property not found.");

                bool canWrite =
                    tenancy.Apartment.Property.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m =>
                        m.PropertyId == tenancy.Apartment.PropertyId &&
                        m.ManagerId == userId &&
                        m.Permission == PermissionLevelEnum.ReadWrite) ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        o.ApartmentId == tenancy.ApartmentId &&
                        o.OwnerId == userId &&
                        o.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite) return Forbid();

                var member = await _context.TenancyMembers
                    .FirstOrDefaultAsync(m => m.Id == memberId && m.TenancyId == tenancyId);

                if (member == null) return NotFound("Tenancy member not found.");

                member.IsDeleted = true;
                member.UpdatedBy = userId;
                member.UpdatedAt = DateTimeOffset.UtcNow;

                _context.TenancyMembers.Update(member);
                await _context.SaveChangesAsync();

                return Ok(new { Message = "Member removed successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpGet("{id}/overview")]
        [Authorize]
        public async Task<IActionResult> GetTenancyOverview(int id)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                var tenancy = await _context.Tenancies
                    .Include(t => t.Apartment)
                    .ThenInclude(a => a.Property)
                    .Include(t => t.Members)
                    .ThenInclude(m => m.Member)
                    .FirstOrDefaultAsync(t => t.Id == id && !t.IsDeleted);

                if (tenancy == null) return NotFound("Tenancy not found.");
                if (tenancy.Apartment == null) return NotFound("Apartment not found.");
                if (tenancy.Apartment.Property == null) return NotFound("Property not found.");

                var apartment = tenancy.Apartment;
                var property = tenancy.Apartment.Property;

                // Access: tenant (primary), member, landlord, owner, manager
                bool hasAccess =
                    tenancy.TenantId == userId ||
                    tenancy.Members.Any(m => !m.IsDeleted && m.MemberId == userId) ||
                    property.LandlordId == userId ||
                    await _context.ApartmentOwners.AnyAsync(o => o.ApartmentId == tenancy.ApartmentId && o.OwnerId == userId && !o.IsDeleted) ||
                    await _context.PropertyManagerAssignments.AnyAsync(m => m.PropertyId == property.Id && m.ManagerId == userId && !m.IsDeleted);

                if (!hasAccess) return Forbid();

                // CanWrite: landlord OR manager RW OR owner RW
                bool canWrite =
                    property.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m =>
                        m.PropertyId == property.Id &&
                        m.ManagerId == userId &&
                        !m.IsDeleted &&
                        m.Permission == PermissionLevelEnum.ReadWrite) ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        o.ApartmentId == tenancy.ApartmentId &&
                        o.OwnerId == userId &&
                        !o.IsDeleted &&
                        o.Permission == PermissionLevelEnum.ReadWrite);

                // Members DTO
                var members = tenancy.Members
                    .Where(m => !m.IsDeleted)
                    .OrderByDescending(m => m.CreatedAt)
                    .Select(m => new TenancyMemberDto
                    {
                        Id = m.Id,
                        TenancyId = m.TenancyId,
                        MemberId = m.MemberId,
                        FullName = m.Member != null ? (m.Member.FullName ?? m.Member.Email ?? "") : "",
                        CreatedAt = m.CreatedAt
                    })
                    .ToList();

                // Documents (tenancy contracts etc.)
                var docs = await _context.Documents
                    .Where(d => d.TenancyId == tenancy.Id && !d.IsDeleted)
                    .OrderByDescending(d => d.CreatedAt)
                    .Select(d => new DocumentDto
                    {
                        Id = d.Id,
                        FileName = d.FileName,
                        BlobUrl = d.BlobUrl,
                        DocumentType = d.DocumentType,
                        UploadedAt = d.UploadedAt,
                        PropertyId = d.PropertyId,
                        ApartmentId = d.ApartmentId,
                        TenancyId = d.TenancyId
                    })
                    .ToListAsync();

                var dto = new TenancyOverviewDto
                {
                    Tenancy = new TenancyDetailsDto
                    {
                        Id = tenancy.Id,
                        ApartmentId = tenancy.ApartmentId,
                        ApartmentName = apartment.Name,
                        PropertyId = apartment.PropertyId,
                        PropertyName = property.Name,
                        StartDate = tenancy.StartDate,
                        EndDate = tenancy.EndDate,
                        MonthlyRent = tenancy.MonthlyRent,
                        MaxMembers = tenancy.MaxMembers,
                        CanWrite = canWrite
                    },
                    Members = members,
                    Documents = docs
                };

                return Ok(dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }
    }
}

