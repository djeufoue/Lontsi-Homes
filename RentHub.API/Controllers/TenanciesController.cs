using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Models.Entities;
using Common.Enums;
using Common.CommunicationModels;
using Common.Helpers;
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

                var tenancyEntities = await _context.Tenancies
                    .Include(t => t.Apartment)
                    .ThenInclude(a => a.Property)
                    .Include(t => t.Members)
                    .Where(t =>
                        t.Members.Any(m => !m.IsDeleted && m.MemberId == userId) ||
                        t.Apartment!.Property!.LandlordId == userId ||
                        _context.ApartmentOwners.Any(o => !o.IsDeleted && o.ApartmentId == t.ApartmentId && o.OwnerId == userId) ||
                        _context.PropertyManagerAssignments.Any(m => !m.IsDeleted && m.PropertyId == t.Apartment!.PropertyId && m.ManagerId == userId))
                    .ToListAsync();

                var tenancies = tenancyEntities
                    .Select(t => new TenancyDto
                    {
                        Id = t.Id,
                        ApartmentName = t.Apartment!.Name,
                        PropertyName = t.Apartment.Property!.Name,
                        StartDate = t.StartDate,
                        EndDate = t.EndDate,
                        MonthlyRent = t.MonthlyRent,
                        MaxMembers = t.MaxMembers,
                        RentDueDay = t.RentDueDay,
                        EndBehavior = t.EndBehavior,
                        TerminatedAt = t.TerminatedAt,
                        Status = ResolveTenancyStatus(t, DateTimeOffset.UtcNow),
                        IsOwner = t.Apartment.Property.LandlordId == userId
                    })
                    .ToList();

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

                if (request.RentDueDay is < 1 or > 31)
                    return BadRequest("RentDueDay must be between 1 and 31.");

                if (request.EndBehavior == TenancyEndBehaviorEnum.ExpireAutomatically && !request.EndDate.HasValue)
                    return BadRequest("EndDate is required when the tenancy should expire automatically.");

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
                    RentDueDay = request.RentDueDay,
                    EndBehavior = request.EndBehavior,
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
                    RentDueDay = tenancy.RentDueDay,
                    EndBehavior = tenancy.EndBehavior,
                    Status = ResolveTenancyStatus(tenancy, DateTimeOffset.UtcNow),
                    IsOwner = apartment.Property.LandlordId == userId
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPost("guided")]
        [Authorize]
        public async Task<IActionResult> CreateGuidedTenancy([FromBody] CreateGuidedTenancyRequest request)
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                if (request.ApartmentId <= 0)
                    return BadRequest("ApartmentId is required.");

                if (request.MonthlyRent <= 0)
                    return BadRequest("Monthly rent must be greater than 0.");

                if (request.MaxMembers <= 0)
                    return BadRequest("MaxMembers must be greater than 0.");

                if (request.RentDueDay is < 1 or > 31)
                    return BadRequest("Rent due day must be between 1 and 31.");

                if (request.EndBehavior == TenancyEndBehaviorEnum.ExpireAutomatically && !request.EndDate.HasValue)
                    return BadRequest("End date is required when the tenancy should expire automatically.");

                if (request.EndDate.HasValue && request.EndDate.Value < request.StartDate)
                    return BadRequest("EndDate cannot be earlier than StartDate.");

                if (request.RentPeriods.Count == 0)
                    return BadRequest("At least one rent period must be generated before creating the tenancy.");

                var tenantEmail = (request.MainTenant.Email ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(tenantEmail))
                    return BadRequest("A main tenant email is required.");

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

                var hasOverlap = await HasOverlappingTenancyAsync(request.ApartmentId, request.StartDate, request.EndDate);
                if (hasOverlap)
                    return BadRequest("This apartment already has a tenancy that overlaps with the selected period.");

                var periodValidationErrors = ValidateRentPeriodSeeds(request.RentPeriods, request.StartDate, request.EndDate, request.EndBehavior);
                if (periodValidationErrors.Count > 0)
                    return BadRequest(new { Message = string.Join(" ", periodValidationErrors) });

                var tenancy = new Tenancy
                {
                    ApartmentId = request.ApartmentId,
                    StartDate = request.StartDate,
                    EndDate = request.EndDate,
                    MonthlyRent = request.MonthlyRent,
                    MaxMembers = request.MaxMembers,
                    RentDueDay = request.RentDueDay,
                    EndBehavior = request.EndBehavior,
                    CreatedBy = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsDeleted = false
                };

                _context.Tenancies.Add(tenancy);
                await _context.SaveChangesAsync();

                var rentPeriods = request.RentPeriods
                    .OrderBy(period => period.PeriodStart)
                    .Select(period => new RentPeriod
                    {
                        TenancyId = tenancy.Id,
                        PeriodStart = period.PeriodStart,
                        PeriodEnd = period.PeriodEnd,
                        DueDate = period.DueDate,
                        Amount = period.Amount,
                        PaidAmount = period.PaidAmount,
                        PaidDate = period.PaidDate,
                        Status = period.Status,
                        CreatedBy = userId,
                        CreatedAt = DateTimeOffset.UtcNow,
                        IsDeleted = false
                    })
                    .ToList();

                _context.RentPeriods.AddRange(rentPeriods);

                var memberUser = (await _userOnboardingService.EnsureUserAsync(
                    tenantEmail,
                    request.MainTenant.FullName,
                    request.MainTenant.CountryCode,
                    request.MainTenant.PhoneNumber,
                    request.MainTenant.WhatsAppPhoneNumber,
                    "Tenant")).User;

                _context.TenancyMembers.Add(new TenancyMember
                {
                    TenancyId = tenancy.Id,
                    MemberId = memberUser.Id,
                    Role = TenancyMemberRoleEnum.MainTenant,
                    CreatedBy = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsDeleted = false
                });

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new TenancyDto
                {
                    Id = tenancy.Id,
                    ApartmentName = apartment.Name,
                    PropertyName = apartment.Property.Name,
                    StartDate = tenancy.StartDate,
                    EndDate = tenancy.EndDate,
                    MonthlyRent = tenancy.MonthlyRent,
                    MaxMembers = tenancy.MaxMembers,
                    RentDueDay = tenancy.RentDueDay,
                    EndBehavior = tenancy.EndBehavior,
                    Status = ResolveTenancyStatus(tenancy, DateTimeOffset.UtcNow),
                    IsOwner = apartment.Property.LandlordId == userId
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
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

                if (request.RentDueDay is < 1 or > 31)
                    return BadRequest("RentDueDay must be between 1 and 31.");

                if (request.EndBehavior == TenancyEndBehaviorEnum.ExpireAutomatically && !request.EndDate.HasValue)
                    return BadRequest("EndDate is required when the tenancy should expire automatically.");

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
                tenancy.RentDueDay = request.RentDueDay;
                tenancy.EndBehavior = request.EndBehavior;
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
                        RentDueDay = t.RentDueDay,
                        EndBehavior = t.EndBehavior,
                        TerminatedAt = t.TerminatedAt,
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
                    request.WhatsAppPhoneNumber,
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
                var rentPeriods = await _context.RentPeriods
                    .Include(period => period.Payment)
                    .Where(period => period.TenancyId == tenancy.Id && !period.IsDeleted)
                    .OrderBy(period => period.PeriodStart)
                    .ToListAsync();

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
                        RentDueDay = tenancy.RentDueDay,
                        EndBehavior = tenancy.EndBehavior,
                        TerminatedAt = tenancy.TerminatedAt,
                        TerminationReason = tenancy.TerminationReason,
                        TerminationNotes = tenancy.TerminationNotes,
                        Status = ResolveTenancyStatus(tenancy, DateTimeOffset.UtcNow),
                        CanWrite = canWrite
                    },
                    Members = members,
                    Documents = docs,
                    RentPeriods = MapRentPeriods(rentPeriods, DateTimeOffset.UtcNow)
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpGet("tenant-dashboard")]
        [Authorize]
        public async Task<IActionResult> GetTenantDashboard()
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                var tenancies = await _context.Tenancies
                    .Include(t => t.Apartment)
                    .ThenInclude(a => a.Property)
                    .ThenInclude(p => p.Landlord)
                    .Include(t => t.RentPeriods)
                    .ThenInclude(period => period.Payment)
                    .Include(t => t.Members)
                    .Where(t => t.Members.Any(m => !m.IsDeleted && m.MemberId == userId))
                    .OrderByDescending(t => t.StartDate)
                    .ToListAsync();

                var tenancyIds = tenancies.Select(t => t.Id).ToList();
                var payments = await _context.Payments
                    .Where(payment => payment.TenantId == userId && payment.TenancyId != null && tenancyIds.Contains(payment.TenancyId.Value))
                    .OrderByDescending(payment => payment.PaymentDate)
                    .ToListAsync();

                var documents = await _context.Documents
                    .Where(document => document.TenancyId != null && tenancyIds.Contains(document.TenancyId.Value) && !document.IsDeleted)
                    .OrderByDescending(document => document.CreatedAt)
                    .ToListAsync();

                var documentDtos = await DocumentHelpers.ToDtosAsync(documents, _storageService);
                var nowUtc = DateTimeOffset.UtcNow;

                var dashboard = new TenantDashboardDto
                {
                    Tenancies = tenancies.Select(tenancy =>
                    {
                        var periods = tenancy.RentPeriods
                            .Where(period => !period.IsDeleted)
                            .OrderBy(period => period.PeriodStart)
                            .ToList();
                        var periodDtos = MapRentPeriods(periods, nowUtc);
                        var outstanding = periodDtos
                            .Where(period => !RentPeriodScheduleHelper.IsPaidStatus(period.Status) && period.Status != RentPeriodStatusEnum.PendingPayment)
                            .Sum(period => period.Amount - period.PaidAmount);
                        var nextDue = periodDtos
                            .Where(period => !RentPeriodScheduleHelper.IsPaidStatus(period.Status))
                            .OrderBy(period => period.PeriodStart)
                            .Select(period => (DateTimeOffset?)period.DueDate)
                            .FirstOrDefault();

                        return new TenantDashboardTenancyDto
                        {
                            Tenancy = new TenancyDetailsDto
                            {
                                Id = tenancy.Id,
                                ApartmentId = tenancy.ApartmentId,
                                ApartmentName = tenancy.Apartment?.Name ?? string.Empty,
                                PropertyId = tenancy.Apartment?.PropertyId ?? 0,
                                PropertyName = tenancy.Apartment?.Property?.Name ?? string.Empty,
                                StartDate = tenancy.StartDate,
                                EndDate = tenancy.EndDate,
                                MonthlyRent = tenancy.MonthlyRent,
                                MaxMembers = tenancy.MaxMembers,
                                RentDueDay = tenancy.RentDueDay,
                                EndBehavior = tenancy.EndBehavior,
                                TerminatedAt = tenancy.TerminatedAt,
                                TerminationReason = tenancy.TerminationReason,
                                TerminationNotes = tenancy.TerminationNotes,
                                Status = ResolveTenancyStatus(tenancy, nowUtc),
                                CanWrite = false
                            },
                            LandlordName = tenancy.Apartment?.Property?.Landlord?.FullName ?? tenancy.Apartment?.Property?.Landlord?.Email ?? "Landlord",
                            LandlordEmail = tenancy.Apartment?.Property?.Landlord?.Email ?? string.Empty,
                            OutstandingBalance = outstanding,
                            NextDueDate = nextDue,
                            RentPeriods = periodDtos,
                            PaymentHistory = MapPaymentHistory(payments.Where(payment => payment.TenancyId == tenancy.Id), periods),
                            Documents = documentDtos.Where(document => document.TenancyId == tenancy.Id).ToList()
                        };
                    }).ToList()
                };

                return Ok(dashboard);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPost("{id}/terminate")]
        [Authorize]
        public async Task<IActionResult> TerminateTenancy(int id, [FromBody] TerminateTenancyRequest request)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                var tenancy = await _context.Tenancies
                    .Include(t => t.Apartment)
                    .ThenInclude(a => a.Property)
                    .Include(t => t.RentPeriods)
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

                if (request.TerminationDate < tenancy.StartDate)
                    return BadRequest("Termination date cannot be before the tenancy start date.");

                tenancy.TerminatedAt = request.TerminationDate;
                tenancy.TerminationReason = request.Reason;
                tenancy.TerminationNotes = request.Notes;
                tenancy.TerminatedBy = userId;
                tenancy.UpdatedBy = userId;
                tenancy.UpdatedAt = DateTimeOffset.UtcNow;

                foreach (var period in tenancy.RentPeriods.Where(period => !period.IsDeleted && period.PeriodStart.Date > request.TerminationDate.Date))
                {
                    if (request.FutureRentHandling == FutureRentHandlingEnum.CancelFutureUnpaidPeriods &&
                        !RentPeriodScheduleHelper.IsPaidStatus(period.Status))
                    {
                        period.Status = RentPeriodStatusEnum.Cancelled;
                        period.UpdatedBy = userId;
                        period.UpdatedAt = DateTimeOffset.UtcNow;
                    }
                }

                foreach (var period in tenancy.RentPeriods.Where(period => request.WaivedRentPeriodIds.Contains(period.Id)))
                {
                    if (!RentPeriodScheduleHelper.IsPaidStatus(period.Status))
                    {
                        period.Status = RentPeriodStatusEnum.Waived;
                        period.PaidAmount = period.Amount;
                        period.PaidDate = DateTimeOffset.UtcNow;
                        period.UpdatedBy = userId;
                        period.UpdatedAt = DateTimeOffset.UtcNow;
                    }
                }

                await _context.SaveChangesAsync();
                return Ok(new { Message = "Tenancy terminated successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        private static List<string> ValidateRentPeriodSeeds(
            IReadOnlyCollection<RentPeriodSeedDto> periods,
            DateTimeOffset tenancyStart,
            DateTimeOffset? tenancyEnd,
            TenancyEndBehaviorEnum endBehavior)
        {
            var errors = new List<string>();
            var ordered = periods.OrderBy(period => period.PeriodStart).ToList();

            if (ordered.Any(period => period.Amount <= 0))
            {
                errors.Add("Every rent period must have an amount greater than 0.");
            }

            if (ordered.First().PeriodStart.Date != tenancyStart.Date)
            {
                errors.Add("The first rent period must start on the tenancy start date.");
            }

            if (endBehavior == TenancyEndBehaviorEnum.ExpireAutomatically &&
                tenancyEnd.HasValue &&
                ordered.Last().PeriodEnd.Date > tenancyEnd.Value.Date)
            {
                errors.Add("Rent periods cannot extend beyond the end date when the tenancy expires automatically.");
            }

            for (var index = 1; index < ordered.Count; index++)
            {
                var previous = ordered[index - 1];
                var current = ordered[index];
                if (current.PeriodStart.Date != previous.PeriodStart.Date.AddMonths(1))
                {
                    errors.Add("Rent periods must be continuous month by month without skipped periods.");
                    break;
                }

                if (previous.PeriodEnd.Date >= current.PeriodStart.Date)
                {
                    errors.Add("Rent periods cannot overlap.");
                    break;
                }
            }

            return errors;
        }

        private static List<RentPeriodDto> MapRentPeriods(IEnumerable<RentPeriod> periods, DateTimeOffset nowUtc)
        {
            var ordered = periods.OrderBy(period => period.PeriodStart).ToList();
            var firstUnpaidId = ordered
                .Where(period => !RentPeriodScheduleHelper.IsPaidStatus(ResolveDisplayStatus(period, nowUtc)))
                .OrderBy(period => period.PeriodStart)
                .Select(period => (int?)period.Id)
                .FirstOrDefault();

            return ordered.Select(period =>
            {
                var status = ResolveDisplayStatus(period, nowUtc);
                var isPaid = RentPeriodScheduleHelper.IsPaidStatus(status);
                var isPayable = !isPaid && firstUnpaidId == period.Id && status != RentPeriodStatusEnum.PendingPayment;

                return new RentPeriodDto
                {
                    Id = period.Id,
                    TenancyId = period.TenancyId,
                    PeriodStart = period.PeriodStart,
                    PeriodEnd = period.PeriodEnd,
                    DueDate = period.DueDate,
                    Amount = period.Amount,
                    PaidAmount = period.PaidAmount,
                    PaidDate = period.PaidDate,
                    PaymentId = period.PaymentId,
                    PaymentReference = period.PaymentReference,
                    SystemReceiptNumber = period.Payment?.SystemReceiptNumber ?? string.Empty,
                    HasSystemReceipt = !string.IsNullOrWhiteSpace(period.Payment?.SystemReceiptNumber),
                    HasProviderReceipt = !string.IsNullOrWhiteSpace(period.Payment?.ProviderReceiptUrl),
                    Status = status,
                    StatusLabel = RentPeriodScheduleHelper.StatusLabel(status),
                    IsPayable = isPayable,
                    LockedReason = isPaid || isPayable
                        ? string.Empty
                        : firstUnpaidId.HasValue
                            ? "Pay previous periods first"
                            : string.Empty
                };
            }).ToList();
        }

        private static RentPeriodStatusEnum ResolveDisplayStatus(RentPeriod period, DateTimeOffset nowUtc)
        {
            if (RentPeriodScheduleHelper.IsPaidStatus(period.Status) ||
                period.Status == RentPeriodStatusEnum.PendingPayment)
            {
                return period.Status;
            }

            return RentPeriodScheduleHelper.ResolveUnpaidStatus(period.DueDate, nowUtc);
        }

        private static List<TenantPaymentHistoryDto> MapPaymentHistory(IEnumerable<Payment> payments, IReadOnlyCollection<RentPeriod> periods)
        {
            return payments.Select(payment =>
            {
                var coveredPeriods = periods
                    .Where(period => period.PaymentId == payment.Id)
                    .OrderBy(period => period.PeriodStart)
                    .ToList();

                var label = coveredPeriods.Count == 0
                    ? "Rent payment"
                    : coveredPeriods.Count == 1
                        ? $"{coveredPeriods[0].PeriodStart:MMM d, yyyy} - {coveredPeriods[0].PeriodEnd:MMM d, yyyy}"
                        : $"{coveredPeriods.First().PeriodStart:MMM d, yyyy} - {coveredPeriods.Last().PeriodEnd:MMM d, yyyy}";

                return new TenantPaymentHistoryDto
                {
                    PaymentId = payment.Id,
                    TenancyId = payment.TenancyId,
                    PaymentDate = payment.PaymentDate,
                    Amount = payment.Amount,
                    Currency = payment.Currency,
                    Method = payment.Method,
                    Status = payment.Status,
                    TransactionId = payment.TransactionId,
                    PeriodLabel = label,
                    SystemReceiptNumber = payment.SystemReceiptNumber ?? string.Empty,
                    HasSystemReceipt = !string.IsNullOrWhiteSpace(payment.SystemReceiptNumber),
                    HasProviderReceipt = !string.IsNullOrWhiteSpace(payment.ProviderReceiptUrl)
                };
            }).ToList();
        }

        private static string ResolveTenancyStatus(Tenancy tenancy, DateTimeOffset nowUtc)
        {
            if (tenancy.TerminatedAt.HasValue)
            {
                return "Terminated";
            }

            if (tenancy.StartDate.Date > nowUtc.Date)
            {
                return "Upcoming";
            }

            if (tenancy.EndDate.HasValue && tenancy.EndDate.Value.Date < nowUtc.Date)
            {
                return tenancy.EndBehavior == TenancyEndBehaviorEnum.ContinueMonthToMonth
                    ? "Month-to-month"
                    : "Expired";
            }

            return "Active";
        }

        private Task<bool> HasOverlappingTenancyAsync(int apartmentId, DateTimeOffset startDate, DateTimeOffset? endDate, int? ignoredTenancyId = null)
        {
            return _context.Tenancies
                .IgnoreQueryFilters()
                .Where(t => !t.IsDeleted && t.ApartmentId == apartmentId && (!ignoredTenancyId.HasValue || t.Id != ignoredTenancyId.Value))
                .Where(t => t.TerminatedAt == null)
                .AnyAsync(t =>
                    (endDate == null || t.StartDate <= endDate.Value) &&
                    (t.EndDate == null || startDate <= t.EndDate.Value));
        }
    }
}
