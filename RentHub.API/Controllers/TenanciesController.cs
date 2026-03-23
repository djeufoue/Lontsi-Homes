using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Models.Entities;
using Common.Enums;
using Common.CommunicationModels;
using RentHub.API.Services.Storage;
using RentHub.API.Helpers;
using RentHub.API.Services.Users;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TenanciesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IStorageService _storageService;
        private readonly IUserOnboardingService _userOnboardingService;

        public TenanciesController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IStorageService storageService,
            IUserOnboardingService userOnboardingService)
        {
            _context = context;
            _userManager = userManager;
            _storageService = storageService;
            _userOnboardingService = userOnboardingService;
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetTenancies()
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId))
                {
                    return Unauthorized();
                }

                var tenancies = await _context.Tenancies
                    .Include(t => t.Apartment)
                    .ThenInclude(a => a.Property)
                    .Include(t => t.Members)
                    .Where(t =>
                        t.Members.Any(m => !m.IsDeleted && m.MemberId == userId) ||
                        t.Apartment!.Property!.LandlordId == userId ||
                        _context.ApartmentOwners.Any(o => !o.IsDeleted && o.ApartmentId == t.ApartmentId && o.OwnerId == userId) ||
                        _context.PropertyManagerAssignments.Any(m => !m.IsDeleted && m.PropertyId == t.Apartment!.PropertyId && m.ManagerId == userId))
                    .Select(t => new TenancyDto
                    {
                        Id = t.Id,
                        ApartmentName = t.Apartment!.Name,
                        PropertyName = t.Apartment.Property!.Name,
                        StartDate = t.StartDate,
                        EndDate = t.EndDate,
                        MonthlyRent = t.MonthlyRent,
                        MaxMembers = t.MaxMembers,
                        IsOwner = t.Apartment.Property.LandlordId == userId
                    })
                    .ToListAsync();

                return Ok(tenancies);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

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
                    .FirstOrDefaultAsync(a => a.Id == request.ApartmentId && !a.IsDeleted);

                if (apartment == null)
                    return NotFound("Apartment not found.");

                if (apartment.Property == null)
                    return NotFound("Property not found.");

                var canWrite =
                    apartment.Property.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m =>
                        !m.IsDeleted &&
                        m.PropertyId == apartment.PropertyId &&
                        m.ManagerId == userId &&
                        m.Permission == PermissionLevelEnum.ReadWrite) ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        !o.IsDeleted &&
                        o.ApartmentId == request.ApartmentId &&
                        o.OwnerId == userId &&
                        o.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite)
                    return Forbid();

                var hasApartmentOwner = await _context.ApartmentOwners.AnyAsync(o =>
                    !o.IsDeleted &&
                    o.ApartmentId == request.ApartmentId &&
                    o.Role == ApartmentMemberRoleEnum.Owner);

                if (hasApartmentOwner)
                    return BadRequest("A tenancy cannot be created for an apartment that already has an apartment member with the Owner role.");

                if (request.EndDate.HasValue && request.EndDate.Value < request.StartDate)
                    return BadRequest("EndDate cannot be earlier than StartDate.");

                if (request.MaxMembers <= 0)
                    return BadRequest("MaxMembers must be greater than 0.");

                var hasOverlap = await HasOverlappingTenancyAsync(request.ApartmentId, request.StartDate, request.EndDate);
                if (hasOverlap)
                    return BadRequest("This apartment already has a tenancy that overlaps with the selected period.");

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

                return Ok(new TenancyDto
                {
                    Id = tenancy.Id,
                    ApartmentName = apartment.Name,
                    PropertyName = apartment.Property.Name,
                    StartDate = tenancy.StartDate,
                    EndDate = tenancy.EndDate,
                    MonthlyRent = tenancy.MonthlyRent,
                    MaxMembers = tenancy.MaxMembers,
                    IsOwner = apartment.Property.LandlordId == userId
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPut("{id}")]
        [Authorize]
        public async Task<IActionResult> UpdateTenancy(int id, [FromBody] UpdateTenancyRequest request)
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
                    .FirstOrDefaultAsync(t => t.Id == id && !t.IsDeleted);

                if (tenancy == null)
                    return NotFound("Tenancy not found.");

                if (tenancy.Apartment?.Property == null)
                    return NotFound("Property not found.");

                var canWrite =
                    tenancy.Apartment.Property.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m =>
                        !m.IsDeleted &&
                        m.PropertyId == tenancy.Apartment.PropertyId &&
                        m.ManagerId == userId &&
                        m.Permission == PermissionLevelEnum.ReadWrite) ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        !o.IsDeleted &&
                        o.ApartmentId == tenancy.ApartmentId &&
                        o.OwnerId == userId &&
                        o.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite)
                    return Forbid();

                if (request.EndDate.HasValue && request.EndDate.Value < request.StartDate)
                    return BadRequest("EndDate cannot be earlier than StartDate.");

                if (request.MaxMembers <= 0)
                    return BadRequest("MaxMembers must be greater than 0.");

                var hasOverlap = await HasOverlappingTenancyAsync(
                    tenancy.ApartmentId,
                    request.StartDate,
                    request.EndDate,
                    tenancy.Id);

                if (hasOverlap)
                    return BadRequest("This apartment already has a tenancy that overlaps with the selected period.");

                tenancy.StartDate = request.StartDate;
                tenancy.EndDate = request.EndDate;
                tenancy.MonthlyRent = request.MonthlyRent;
                tenancy.MaxMembers = request.MaxMembers;
                tenancy.UpdatedBy = userId;
                tenancy.UpdatedAt = DateTimeOffset.UtcNow;

                _context.Tenancies.Update(tenancy);
                await _context.SaveChangesAsync();

                return Ok(new { Message = "Tenancy updated successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPut("{tenancyId}/max-members")]
        [Authorize]
        public async Task<IActionResult> UpdateMaxTenancyMembers(int tenancyId, [FromBody] int maxMembers)
        {
            try
            {
                if (maxMembers <= 0)
                    return BadRequest("MaxMembers must be a positive integer.");

                var tenancy = await _context.Tenancies
                    .Include(t => t.Apartment)
                    .ThenInclude(a => a.Property)
                    .FirstOrDefaultAsync(t => t.Id == tenancyId && !t.IsDeleted);

                if (tenancy == null)
                    return NotFound("Tenancy not found.");

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                var canWrite =
                    tenancy.Apartment!.Property!.LandlordId == userId ||
                    await _context.ApartmentOwners.AnyAsync(o => !o.IsDeleted && o.ApartmentId == tenancy.ApartmentId && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite) ||
                    await _context.PropertyManagerAssignments.AnyAsync(m => !m.IsDeleted && m.PropertyId == tenancy.Apartment.PropertyId && m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite)
                    return Forbid();

                tenancy.MaxMembers = maxMembers;
                tenancy.UpdatedBy = userId;
                tenancy.UpdatedAt = DateTimeOffset.UtcNow;

                _context.Tenancies.Update(tenancy);
                await _context.SaveChangesAsync();

                return Ok(new { Message = "MaxMembers updated successfully.", TenancyId = tenancy.Id, MaxMembers = tenancy.MaxMembers });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPut("{id}/extend")]
        [Authorize]
        public async Task<IActionResult> ExtendTenancy(int id, [FromBody] ExtendTenancyRequest request)
        {
            try
            {
                var tenancy = await _context.Tenancies
                    .Include(t => t.Apartment!)
                    .ThenInclude(a => a.Property)
                    .FirstOrDefaultAsync(t => t.Id == id && !t.IsDeleted);

                if (tenancy == null)
                    return NotFound("Tenancy not found.");

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                if (request.NewEndDate <= tenancy.StartDate)
                    return BadRequest("New end date must be after the tenancy start date.");

                var canWrite =
                    tenancy.Apartment!.Property!.LandlordId == userId ||
                    await _context.ApartmentOwners.AnyAsync(o => !o.IsDeleted && o.ApartmentId == tenancy.ApartmentId && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite) ||
                    await _context.PropertyManagerAssignments.AnyAsync(m => !m.IsDeleted && m.PropertyId == tenancy.Apartment.PropertyId && m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite)
                    return Forbid();

                tenancy.EndDate = request.NewEndDate;
                tenancy.UpdatedBy = userId;
                tenancy.UpdatedAt = DateTimeOffset.UtcNow;
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

                var apartment = await _context.Apartments
                    .Include(a => a.Property)
                    .FirstOrDefaultAsync(a => a.Id == apartmentId && !a.IsDeleted);

                if (apartment == null) return NotFound("Apartment not found.");

                var hasAccess =
                    apartment.Property?.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m => !m.IsDeleted && m.PropertyId == apartment.PropertyId && m.ManagerId == userId) ||
                    await _context.ApartmentOwners.AnyAsync(o => !o.IsDeleted && o.ApartmentId == apartmentId && o.OwnerId == userId) ||
                    await _context.Tenancies.AnyAsync(t =>
                        !t.IsDeleted &&
                        t.ApartmentId == apartmentId &&
                        t.Members.Any(mm => !mm.IsDeleted && mm.MemberId == userId));

                if (!hasAccess) return Forbid();

                var list = await _context.Tenancies
                    .Where(t => t.ApartmentId == apartmentId && !t.IsDeleted)
                    .OrderByDescending(t => t.StartDate)
                    .Select(t => new TenancyDto
                    {
                        Id = t.Id,
                        ApartmentName = apartment.Name,
                        PropertyName = apartment.Property!.Name,
                        StartDate = t.StartDate,
                        EndDate = t.EndDate,
                        MonthlyRent = t.MonthlyRent,
                        MaxMembers = t.MaxMembers,
                        IsOwner = apartment.Property!.LandlordId == userId
                    })
                    .ToListAsync();

                return Ok(list);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

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
                    .ThenInclude(m => m.Member)
                    .FirstOrDefaultAsync(t => t.Id == tenancyId && !t.IsDeleted);

                if (tenancy == null) return NotFound("Tenancy not found.");
                if (tenancy.Apartment?.Property == null) return NotFound("Property not found.");

                var canWrite =
                    tenancy.Apartment.Property.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m =>
                        !m.IsDeleted &&
                        m.PropertyId == tenancy.Apartment.PropertyId &&
                        m.ManagerId == userId &&
                        m.Permission == PermissionLevelEnum.ReadWrite) ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        !o.IsDeleted &&
                        o.ApartmentId == tenancy.ApartmentId &&
                        o.OwnerId == userId &&
                        o.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite) return Forbid();

                var currentCount = tenancy.Members.Count(m => !m.IsDeleted);
                if (currentCount >= tenancy.MaxMembers)
                    return BadRequest($"Maximum number of members ({tenancy.MaxMembers}) reached for this tenancy.");

                var email = (request.Email ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(email))
                    return BadRequest("Email is required.");

                var memberUser = (await _userOnboardingService.EnsureUserAsync(
                    email,
                    request.FullName,
                    request.CountryCode,
                    request.PhoneNumber,
                    "Tenant")).User;

                var alreadyMember = tenancy.Members.Any(m => !m.IsDeleted && m.MemberId == memberUser.Id);
                if (alreadyMember)
                    return BadRequest("User is already a member of this tenancy.");

                var isPropertyMember = await _context.PropertyManagerAssignments.AnyAsync(m =>
                    !m.IsDeleted &&
                    m.PropertyId == tenancy.Apartment.PropertyId &&
                    m.ManagerId == memberUser.Id);

                if (isPropertyMember)
                    return BadRequest("Property members cannot also be added as apartment or tenancy members within the same property.");

                var isApartmentMember = await _context.ApartmentOwners.AnyAsync(o =>
                    !o.IsDeleted &&
                    o.ApartmentId == tenancy.ApartmentId &&
                    o.OwnerId == memberUser.Id);

                if (isApartmentMember)
                    return BadRequest("An apartment member cannot also be added as a tenancy member for the same apartment.");

                if (request.Role == TenancyMemberRoleEnum.MainTenant &&
                    tenancy.Members.Any(m => !m.IsDeleted && m.Role == TenancyMemberRoleEnum.MainTenant))
                {
                    return BadRequest("This tenancy already has a main tenant.");
                }

                var member = new TenancyMember
                {
                    TenancyId = tenancyId,
                    MemberId = memberUser.Id,
                    Role = request.Role,
                    CreatedBy = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsDeleted = false
                };

                _context.TenancyMembers.Add(member);
                await _context.SaveChangesAsync();

                return Ok(new TenancyMemberDto
                {
                    Id = member.Id,
                    TenancyId = member.TenancyId,
                    MemberId = member.MemberId,
                    Role = member.Role.ToString(),
                    FullName = memberUser.FullName ?? string.Empty,
                    Email = memberUser.Email ?? string.Empty,
                    CountryCode = memberUser.CountryCode ?? string.Empty,
                    CreatedAt = member.CreatedAt
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPut("{tenancyId}/members/{memberId}")]
        [Authorize]
        public async Task<IActionResult> UpdateTenancyMember(int tenancyId, int memberId, [FromBody] UpdateTenancyMemberRequest request)
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
                    .FirstOrDefaultAsync(t => t.Id == tenancyId && !t.IsDeleted);

                if (tenancy == null) return NotFound("Tenancy not found.");
                if (tenancy.Apartment?.Property == null) return NotFound("Property not found.");

                var canWrite =
                    tenancy.Apartment.Property.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m =>
                        !m.IsDeleted &&
                        m.PropertyId == tenancy.Apartment.PropertyId &&
                        m.ManagerId == userId &&
                        m.Permission == PermissionLevelEnum.ReadWrite) ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        !o.IsDeleted &&
                        o.ApartmentId == tenancy.ApartmentId &&
                        o.OwnerId == userId &&
                        o.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite) return Forbid();

                var member = await _context.TenancyMembers
                    .FirstOrDefaultAsync(m => m.Id == memberId && m.TenancyId == tenancyId && !m.IsDeleted);

                if (member == null)
                    return NotFound("Tenancy member not found.");

                if (request.Role == TenancyMemberRoleEnum.MainTenant &&
                    tenancy.Members.Any(m => !m.IsDeleted && m.Id != memberId && m.Role == TenancyMemberRoleEnum.MainTenant))
                {
                    return BadRequest("This tenancy already has a main tenant.");
                }

                member.Role = request.Role;
                member.UpdatedBy = userId;
                member.UpdatedAt = DateTimeOffset.UtcNow;
                _context.TenancyMembers.Update(member);
                await _context.SaveChangesAsync();

                return Ok(new { Message = "Tenancy member updated successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

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
                    .FirstOrDefaultAsync(t => t.Id == tenancyId && !t.IsDeleted);

                if (tenancy == null) return NotFound("Tenancy not found.");
                if (tenancy.Apartment?.Property == null) return NotFound("Property not found.");

                var canWrite =
                    tenancy.Apartment.Property.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m =>
                        !m.IsDeleted &&
                        m.PropertyId == tenancy.Apartment.PropertyId &&
                        m.ManagerId == userId &&
                        m.Permission == PermissionLevelEnum.ReadWrite) ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        !o.IsDeleted &&
                        o.ApartmentId == tenancy.ApartmentId &&
                        o.OwnerId == userId &&
                        o.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite) return Forbid();

                var member = await _context.TenancyMembers
                    .FirstOrDefaultAsync(m => m.Id == memberId && m.TenancyId == tenancyId && !m.IsDeleted);

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
                if (tenancy.Apartment?.Property == null) return NotFound("Property not found.");

                var hasAccess =
                    tenancy.Members.Any(m => !m.IsDeleted && m.MemberId == userId) ||
                    tenancy.Apartment.Property.LandlordId == userId ||
                    await _context.ApartmentOwners.AnyAsync(o => !o.IsDeleted && o.ApartmentId == tenancy.ApartmentId && o.OwnerId == userId) ||
                    await _context.PropertyManagerAssignments.AnyAsync(m => !m.IsDeleted && m.PropertyId == tenancy.Apartment.PropertyId && m.ManagerId == userId);

                if (!hasAccess) return Forbid();

                var canWrite =
                    tenancy.Apartment.Property.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m =>
                        !m.IsDeleted &&
                        m.PropertyId == tenancy.Apartment.PropertyId &&
                        m.ManagerId == userId &&
                        m.Permission == PermissionLevelEnum.ReadWrite) ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        !o.IsDeleted &&
                        o.ApartmentId == tenancy.ApartmentId &&
                        o.OwnerId == userId &&
                        o.Permission == PermissionLevelEnum.ReadWrite);

                var members = tenancy.Members
                    .Where(m => !m.IsDeleted)
                    .OrderBy(m => m.Role == TenancyMemberRoleEnum.MainTenant ? 0 : 1)
                    .ThenByDescending(m => m.CreatedAt)
                    .Select(m => new TenancyMemberDto
                    {
                        Id = m.Id,
                        TenancyId = m.TenancyId,
                        MemberId = m.MemberId,
                        Role = m.Role.ToString(),
                        FullName = m.Member != null ? (m.Member.FullName ?? m.Member.Email ?? string.Empty) : string.Empty,
                        Email = m.Member?.Email ?? string.Empty,
                        CountryCode = m.Member?.CountryCode ?? string.Empty,
                        CreatedAt = m.CreatedAt
                    })
                    .ToList();

                var documentEntities = await _context.Documents
                    .Where(d => d.TenancyId == tenancy.Id && !d.IsDeleted)
                    .OrderByDescending(d => d.CreatedAt)
                    .ToListAsync();

                var docs = await DocumentHelpers.ToDtosAsync(documentEntities, _storageService);

                return Ok(new TenancyOverviewDto
                {
                    Tenancy = new TenancyDetailsDto
                    {
                        Id = tenancy.Id,
                        ApartmentId = tenancy.ApartmentId,
                        ApartmentName = tenancy.Apartment.Name,
                        PropertyId = tenancy.Apartment.PropertyId,
                        PropertyName = tenancy.Apartment.Property.Name,
                        StartDate = tenancy.StartDate,
                        EndDate = tenancy.EndDate,
                        MonthlyRent = tenancy.MonthlyRent,
                        MaxMembers = tenancy.MaxMembers,
                        CanWrite = canWrite
                    },
                    Members = members,
                    Documents = docs
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        private Task<bool> HasOverlappingTenancyAsync(int apartmentId, DateTimeOffset startDate, DateTimeOffset? endDate, int? ignoredTenancyId = null)
        {
            return _context.Tenancies
                .Where(t => !t.IsDeleted && t.ApartmentId == apartmentId && (!ignoredTenancyId.HasValue || t.Id != ignoredTenancyId.Value))
                .AnyAsync(t =>
                    (endDate == null || t.StartDate <= endDate.Value) &&
                    (t.EndDate == null || startDate <= t.EndDate.Value));
        }
    }
}
