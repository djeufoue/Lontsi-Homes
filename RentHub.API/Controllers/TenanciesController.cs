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
using RentHub.API.Services.Permissions;
using RentHub.API.Services.Reminders;

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
        private readonly IManagerPermissionService _permissionService;
        private readonly IRentReminderService _rentReminderService;

        public TenanciesController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IStorageService storageService,
            IUserOnboardingService userOnboardingService,
            IManagerPermissionService permissionService,
            IRentReminderService rentReminderService)
        {
            _context = context;
            _userManager = userManager;
            _storageService = storageService;
            _userOnboardingService = userOnboardingService;
            _permissionService = permissionService;
            _rentReminderService = rentReminderService;
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

                if (User.IsInRole("Manager") && !User.IsInRole("Admin") && !User.IsInRole("Landlord"))
                {
                    var visible = new List<Tenancy>();
                    foreach (var tenancy in tenancyEntities)
                    {
                        if (tenancy.Members.Any(member => !member.IsDeleted && member.MemberId == userId) ||
                            await _permissionService.HasTenancyPermissionAsync(
                                userId, tenancy.Id, ManagerPermission.ViewTenancies, false))
                        {
                            visible.Add(tenancy);
                        }
                    }

                    tenancyEntities = visible;
                }

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
                    await _permissionService.HasApartmentPermissionAsync(
                        userId, request.ApartmentId, ManagerPermission.AddTenancy, User.IsInRole("Admin")) ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        !o.IsDeleted &&
                        o.ApartmentId == request.ApartmentId &&
                        o.OwnerId == userId &&
                        o.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite)
                    return Forbid();

                if (!User.IsInRole("Admin") &&
                    !await PaymentAvailabilityHelper.HasActiveSubscriptionAsync(_context, apartment.Property.LandlordId))
                    return SubscriptionRequired();

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

                var hasOverlap = await TenancyLifecycleHelper.HasOverlappingTenancyAsync(_context, request.ApartmentId, request.StartDate, request.EndDate);
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
                    await _permissionService.HasApartmentPermissionAsync(
                        userId, request.ApartmentId, ManagerPermission.AddTenancy, User.IsInRole("Admin")) ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        !o.IsDeleted &&
                        o.ApartmentId == request.ApartmentId &&
                        o.OwnerId == userId &&
                        o.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite)
                    return Forbid();

                if (!User.IsInRole("Admin") &&
                    !await PaymentAvailabilityHelper.HasActiveSubscriptionAsync(_context, apartment.Property.LandlordId))
                    return SubscriptionRequired();

                var hasOverlap = await TenancyLifecycleHelper.HasOverlappingTenancyAsync(_context, request.ApartmentId, request.StartDate, request.EndDate);
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

                var isAdmin = User.IsInRole("Admin");
                var apartmentOwnerCanWrite = await _context.ApartmentOwners.AnyAsync(o =>
                        !o.IsDeleted &&
                        o.ApartmentId == tenancy.ApartmentId &&
                        o.OwnerId == userId &&
                        o.Permission == PermissionLevelEnum.ReadWrite);
                var canWrite =
                    await _permissionService.HasTenancyPermissionAsync(
                        userId, id, ManagerPermission.EditTenancy, isAdmin) ||
                    apartmentOwnerCanWrite;

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

                var hasOverlap = await TenancyLifecycleHelper.HasOverlappingTenancyAsync(
                    _context,
                    tenancy.ApartmentId,
                    request.StartDate,
                    request.EndDate,
                    tenancy.Id);

                if (hasOverlap)
                    return BadRequest("This apartment already has a tenancy that overlaps with the selected period.");

                tenancy.StartDate = request.StartDate;
                tenancy.EndDate = request.EndDate;
                var canEditFinancialInformation = apartmentOwnerCanWrite ||
                    await _permissionService.HasApartmentPermissionAsync(
                        userId, tenancy.ApartmentId, ManagerPermission.EditPropertyFinancialInformation, isAdmin);
                if (canEditFinancialInformation)
                {
                    tenancy.MonthlyRent = request.MonthlyRent;
                }
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
                    await _permissionService.HasTenancyPermissionAsync(
                        userId, tenancyId, ManagerPermission.EditTenancy, User.IsInRole("Admin")) ||
                    await _context.ApartmentOwners.AnyAsync(o => !o.IsDeleted && o.ApartmentId == tenancy.ApartmentId && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite);

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

                var canWrite =
                    await _permissionService.HasTenancyPermissionAsync(
                        userId, id, ManagerPermission.RenewTenancy, User.IsInRole("Admin")) ||
                    await _context.ApartmentOwners.AnyAsync(o => !o.IsDeleted && o.ApartmentId == tenancy.ApartmentId && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite)
                    return Forbid();

                var nowUtc = DateTimeOffset.UtcNow;
                if (tenancy.TerminatedAt.HasValue)
                    return Conflict(new { Message = "A terminated tenancy cannot be renewed." });
                if (!tenancy.EndDate.HasValue)
                    return BadRequest(new { Message = "A tenancy without an end date cannot be renewed." });
                if (tenancy.EndDate.Value.Date < nowUtc.Date)
                    return Conflict(new { Message = "An expired tenancy cannot be renewed." });
                if (request.NewEndDate.Date <= tenancy.EndDate.Value.Date)
                    return BadRequest(new { Message = "New end date must be later than the current tenancy end date." });
                if (request.NewEndDate.Date <= nowUtc.Date)
                    return BadRequest(new { Message = "New end date must be in the future." });

                if (await TenancyLifecycleHelper.HasOverlappingTenancyAsync(
                        _context,
                        tenancy.ApartmentId,
                        tenancy.StartDate,
                        request.NewEndDate,
                        tenancy.Id))
                    return BadRequest("The extension would overlap another tenancy for this apartment.");

                await TenancyLifecycleHelper.SynchronizeRentPeriodsForExtensionAsync(
                    _context,
                    tenancy,
                    request.NewEndDate,
                    userId,
                    nowUtc);

                tenancy.EndDate = request.NewEndDate;
                tenancy.RenewalReminderSentAt = null;
                tenancy.RenewalReminderSentForEndDate = null;
                tenancy.UpdatedBy = userId;
                tenancy.UpdatedAt = nowUtc;
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
                    await _permissionService.HasApartmentPermissionAsync(
                        userId, apartmentId, ManagerPermission.ViewTenancies, User.IsInRole("Admin")) ||
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
                    await _permissionService.HasTenancyPermissionAsync(
                        userId, tenancyId, ManagerPermission.AddTenancyMember, User.IsInRole("Admin")) ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        !o.IsDeleted &&
                        o.ApartmentId == tenancy.ApartmentId &&
                        o.OwnerId == userId &&
                        o.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite) return Forbid();

                if (!User.IsInRole("Admin") &&
                    !await PaymentAvailabilityHelper.HasActiveSubscriptionAsync(_context, tenancy.Apartment.Property.LandlordId))
                    return SubscriptionRequired();

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
                    PhoneNumber = memberUser.PhoneNumber ?? string.Empty,
                    WhatsAppPhoneNumber = memberUser.WhatsAppPhoneNumber ?? string.Empty,
                    EmailConfirmed = memberUser.EmailConfirmed,
                    WhatsAppPhoneVerified = memberUser.IsWhatsAppPhoneVerified,
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
                    await _permissionService.HasTenancyPermissionAsync(
                        userId, tenancyId, ManagerPermission.EditTenancyMember, User.IsInRole("Admin")) ||
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
                    await _permissionService.HasTenancyPermissionAsync(
                        userId, tenancyId, ManagerPermission.RemoveTenancyMember, User.IsInRole("Admin")) ||
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
                    User.IsInRole("Admin") ||
                    tenancy.Members.Any(m => !m.IsDeleted && m.MemberId == userId) ||
                    tenancy.Apartment.Property.LandlordId == userId ||
                    await _context.ApartmentOwners.AnyAsync(o => !o.IsDeleted && o.ApartmentId == tenancy.ApartmentId && o.OwnerId == userId) ||
                    await _permissionService.HasTenancyPermissionAsync(
                        userId, id, ManagerPermission.ViewTenancyDetails, User.IsInRole("Admin"));

                if (!hasAccess) return Forbid();

                var isAdmin = User.IsInRole("Admin");
                var isRestrictedManager = User.IsInRole("Manager") &&
                                          !isAdmin &&
                                          tenancy.Apartment.Property.LandlordId != userId;
                var apartmentOwnerCanWrite = await _context.ApartmentOwners.AnyAsync(o =>
                    !o.IsDeleted &&
                    o.ApartmentId == tenancy.ApartmentId &&
                    o.OwnerId == userId &&
                    o.Permission == PermissionLevelEnum.ReadWrite);

                var canEdit = await _permissionService.HasTenancyPermissionAsync(userId, id, ManagerPermission.EditTenancy, isAdmin) || apartmentOwnerCanWrite;
                var canDelete = await _permissionService.HasTenancyPermissionAsync(userId, id, ManagerPermission.DeleteTenancy, isAdmin) || apartmentOwnerCanWrite;
                var canTerminate = await _permissionService.HasTenancyPermissionAsync(userId, id, ManagerPermission.TerminateTenancy, isAdmin) || apartmentOwnerCanWrite;
                var canRenew = await _permissionService.HasTenancyPermissionAsync(userId, id, ManagerPermission.RenewTenancy, isAdmin) || apartmentOwnerCanWrite;
                var canViewMembers = !isRestrictedManager || await _permissionService.HasTenancyPermissionAsync(userId, id, ManagerPermission.ViewTenancyMembers, false);
                var canAddMembers = await _permissionService.HasTenancyPermissionAsync(userId, id, ManagerPermission.AddTenancyMember, isAdmin) || apartmentOwnerCanWrite;
                var canEditMembers = await _permissionService.HasTenancyPermissionAsync(userId, id, ManagerPermission.EditTenancyMember, isAdmin) || apartmentOwnerCanWrite;
                var canRemoveMembers = await _permissionService.HasTenancyPermissionAsync(userId, id, ManagerPermission.RemoveTenancyMember, isAdmin) || apartmentOwnerCanWrite;
                var canViewDocuments = !isRestrictedManager || await _permissionService.HasTenancyPermissionAsync(userId, id, ManagerPermission.ViewLeaseDocuments, false);
                var canUploadDocuments = await _permissionService.HasTenancyPermissionAsync(userId, id, ManagerPermission.UploadLeaseDocuments, isAdmin) || apartmentOwnerCanWrite;
                var canDeleteDocuments = await _permissionService.HasTenancyPermissionAsync(userId, id, ManagerPermission.DeleteLeaseDocuments, isAdmin) || apartmentOwnerCanWrite;
                var canViewRent = !isRestrictedManager || await _permissionService.HasTenancyPermissionAsync(userId, id, ManagerPermission.ViewRentInformation, false);
                var canMarkRentPaid = await _permissionService.HasTenancyPermissionAsync(userId, id, ManagerPermission.MarkRentAsPaid, isAdmin) || apartmentOwnerCanWrite;
                var canCancelPendingPayment = await _permissionService.HasTenancyPermissionAsync(userId, id, ManagerPermission.DeletePayment, isAdmin) || apartmentOwnerCanWrite;
                var canSendRentReminder = await _permissionService.HasTenancyPermissionAsync(userId, id, ManagerPermission.SendRentReminder, isAdmin) || apartmentOwnerCanWrite;
                var canWrite = canEdit || canDelete || canTerminate || canRenew || canAddMembers || canEditMembers ||
                               canRemoveMembers || canUploadDocuments || canDeleteDocuments || canMarkRentPaid ||
                               canCancelPendingPayment || canSendRentReminder;

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
                        PhoneNumber = m.Member?.PhoneNumber ?? string.Empty,
                        WhatsAppPhoneNumber = m.Member?.WhatsAppPhoneNumber ?? string.Empty,
                        EmailConfirmed = m.Member?.EmailConfirmed ?? false,
                        WhatsAppPhoneVerified = m.Member?.IsWhatsAppPhoneVerified ?? false,
                        CreatedAt = m.CreatedAt
                    })
                    .ToList();
                if (!canViewMembers)
                {
                    members.Clear();
                }

                var documentEntities = canViewDocuments
                    ? await _context.Documents
                        .Where(d => d.TenancyId == tenancy.Id && !d.IsDeleted)
                        .OrderByDescending(d => d.CreatedAt)
                        .ToListAsync()
                    : new List<Document>();

                var docs = await DocumentHelpers.ToDtosAsync(documentEntities, _storageService);
                var rentPeriods = canViewRent
                    ? await _context.RentPeriods
                        .Include(period => period.Payment)
                        .Where(period => period.TenancyId == tenancy.Id && !period.IsDeleted)
                        .OrderBy(period => period.PeriodStart)
                        .ToListAsync()
                    : new List<RentPeriod>();

                var reminderEntities = canViewRent
                    ? await _context.RentReminders
                        .AsNoTracking()
                        .Include(reminder => reminder.Periods)
                        .Where(reminder => reminder.TenancyId == tenancy.Id)
                        .OrderByDescending(reminder => reminder.SentAt ?? reminder.CreatedAt)
                        .ToListAsync()
                    : new List<RentReminder>();

                var requesterIds = reminderEntities
                    .Where(reminder => !string.IsNullOrWhiteSpace(reminder.RequestedByUserId))
                    .Select(reminder => reminder.RequestedByUserId!)
                    .Distinct()
                    .ToList();
                var requesterNames = requesterIds.Count == 0
                    ? new Dictionary<string, string>()
                    : await _context.Users
                        .Where(user => requesterIds.Contains(user.Id))
                        .ToDictionaryAsync(
                            user => user.Id,
                            user => user.FullName ?? user.Email ?? string.Empty);
                var reminderHistory = MapReminderHistory(reminderEntities, requesterNames);
                var nowUtc = DateTimeOffset.UtcNow;
                var rentSummary = BuildRentSummary(
                    rentPeriods,
                    reminderHistory,
                    tenancy.Apartment.ManualRentReminderLimit,
                    tenancy.Apartment.ManualRentReminderCooldownHours,
                    canSendRentReminder,
                    nowUtc);

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
                        Status = ResolveTenancyStatus(tenancy, nowUtc),
                        CanWrite = canWrite,
                        CanEdit = canEdit,
                        CanDelete = canDelete,
                        CanTerminate = canTerminate,
                        CanRenew = canRenew,
                        CanViewMembers = canViewMembers,
                        CanAddMembers = canAddMembers,
                        CanEditMembers = canEditMembers,
                        CanRemoveMembers = canRemoveMembers,
                        CanViewDocuments = canViewDocuments,
                        CanUploadDocuments = canUploadDocuments,
                        CanDeleteDocuments = canDeleteDocuments,
                        CanViewRent = canViewRent,
                        CanMarkRentPaid = canMarkRentPaid,
                        CanCancelPendingPayment = canCancelPendingPayment,
                        CanSendRentReminder = canSendRentReminder
                    },
                    Members = members,
                    Documents = docs,
                    RentPeriods = MapRentPeriods(rentPeriods, nowUtc, reminderHistory),
                    RentSummary = rentSummary,
                    ReminderHistory = reminderHistory
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPost("{id}/rent-reminders/manual")]
        [Authorize]
        public async Task<IActionResult> SendManualRentReminder(int id)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            var exists = await _context.Tenancies.AnyAsync(tenancy => tenancy.Id == id && !tenancy.IsDeleted);
            if (!exists) return NotFound("Tenancy not found.");

            var isAdmin = User.IsInRole("Admin");
            var canSend = await _permissionService.HasTenancyPermissionAsync(
                userId,
                id,
                ManagerPermission.SendRentReminder,
                isAdmin) ||
                await _context.ApartmentOwners.AnyAsync(owner =>
                    !owner.IsDeleted &&
                    owner.OwnerId == userId &&
                    owner.Permission == PermissionLevelEnum.ReadWrite &&
                    owner.Apartment!.Tenancies.Any(tenancy => tenancy.Id == id && !tenancy.IsDeleted));

            if (!canSend) return Forbid();

            try
            {
                return Ok(await _rentReminderService.SendManualReminderAsync(id, userId));
            }
            catch (InvalidOperationException exception)
            {
                return BadRequest(new { Message = exception.Message });
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
                var platformAutomaticPaymentsEnabled = await PaymentAvailabilityHelper.IsPlatformAutomaticPaymentEnabledAsync(_context);

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
                        var paymentAvailability = ResolveTenantRentPaymentAvailability(
                            tenancy.Apartment?.Property?.Landlord,
                            tenancy.Apartment?.Property?.CountryIsoCode,
                            tenancy.Apartment?.Property?.CountryCode);
                        if (!platformAutomaticPaymentsEnabled || tenancy.Apartment?.Property?.AutomaticPaymentsEnabled != true)
                        {
                            paymentAvailability = TenantRentPaymentAvailability.Unavailable(
                                PaymentMethodEnum.Cash,
                                PaymentAvailabilityHelper.AutomaticPaymentsUnavailableMessage);
                        }

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
                            PaymentMethod = paymentAvailability.Method,
                            CanPayRent = paymentAvailability.CanPay,
                            PaymentUnavailableReason = paymentAvailability.Message,
                            AutomaticPaymentsEnabled = platformAutomaticPaymentsEnabled && tenancy.Apartment?.Property?.AutomaticPaymentsEnabled == true,
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
                    await _permissionService.HasTenancyPermissionAsync(
                        userId, id, ManagerPermission.TerminateTenancy, User.IsInRole("Admin")) ||
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

        internal static TenantRentPaymentAvailability ResolveTenantRentPaymentAvailability(
            ApplicationUser? landlord,
            string? propertyCountryIsoCode = null,
            string? propertyCountryCode = null)
        {
            if (landlord == null)
            {
                return TenantRentPaymentAvailability.Unavailable(
                    PaymentMethodEnum.Card,
                    "Rent payment is not available because the landlord profile could not be found.");
            }

            if (!IsCameroonPropertyOrProfile(landlord, propertyCountryIsoCode, propertyCountryCode))
            {
                return IsStripePayoutReady(landlord)
                    ? TenantRentPaymentAvailability.Available(PaymentMethodEnum.Card, "Card payment is available.")
                    : TenantRentPaymentAvailability.Unavailable(
                        PaymentMethodEnum.Card,
                        "Card payment mode is not available because the landlord has not completed Stripe payout setup.");
            }

            if (landlord.PayoutChannel == PayoutChannelEnum.MtnMoney &&
                landlord.IsPayoutPhoneVerified &&
                !string.IsNullOrWhiteSpace(landlord.PayoutPhoneNumber))
            {
                return TenantRentPaymentAvailability.Available(PaymentMethodEnum.Momo, "MTN Mobile Money payment is available.");
            }

            if (landlord.PayoutChannel == PayoutChannelEnum.OrangeMoney &&
                landlord.IsPayoutPhoneVerified &&
                !string.IsNullOrWhiteSpace(landlord.PayoutPhoneNumber))
            {
                return TenantRentPaymentAvailability.Available(PaymentMethodEnum.OrangeMoney, "Orange Money payment is available.");
            }

            return IsStripePayoutReady(landlord)
                ? TenantRentPaymentAvailability.Available(PaymentMethodEnum.Card, "Card payment is available.")
                : TenantRentPaymentAvailability.Unavailable(
                    PaymentMethodEnum.Card,
                    "Rent payment is not available because the landlord has not configured a verified payout method.");
        }

        private ObjectResult SubscriptionRequired()
        {
            return StatusCode(StatusCodes.Status402PaymentRequired, new
            {
                Code = "SUBSCRIPTION_PAYMENT_REQUIRED",
                Message = PaymentAvailabilityHelper.SubscriptionRequiredMessage
            });
        }

        internal sealed class TenantRentPaymentAvailability
        {
            public PaymentMethodEnum Method { get; init; } = PaymentMethodEnum.Card;
            public bool CanPay { get; init; }
            public string Message { get; init; } = string.Empty;

            public static TenantRentPaymentAvailability Available(PaymentMethodEnum method, string message)
            {
                return new TenantRentPaymentAvailability
                {
                    Method = method,
                    CanPay = true,
                    Message = message
                };
            }

            public static TenantRentPaymentAvailability Unavailable(PaymentMethodEnum method, string message)
            {
                return new TenantRentPaymentAvailability
                {
                    Method = method,
                    CanPay = false,
                    Message = message
                };
            }
        }

        internal static bool IsStripePayoutReady(ApplicationUser landlord)
        {
            return !string.IsNullOrWhiteSpace(landlord.StripeConnectAccountId) &&
                   landlord.StripePayoutDetailsSubmitted &&
                   landlord.StripeChargesEnabled &&
                   landlord.StripePayoutsEnabled;
        }

        internal static bool IsCameroonProfile(ApplicationUser landlord)
        {
            if (!string.IsNullOrWhiteSpace(landlord.CountryIsoCode))
            {
                return string.Equals(landlord.CountryIsoCode.Trim(), "CM", StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(landlord.CountryCode?.Trim(), "+237", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsCameroonPropertyOrProfile(
            ApplicationUser landlord,
            string? propertyCountryIsoCode,
            string? propertyCountryCode)
        {
            if (!string.IsNullOrWhiteSpace(propertyCountryIsoCode) || !string.IsNullOrWhiteSpace(propertyCountryCode))
            {
                if (!string.IsNullOrWhiteSpace(propertyCountryIsoCode))
                {
                    return string.Equals(propertyCountryIsoCode.Trim(), "CM", StringComparison.OrdinalIgnoreCase);
                }

                return string.Equals(propertyCountryCode?.Trim(), "+237", StringComparison.OrdinalIgnoreCase);
            }

            return IsCameroonProfile(landlord);
        }

        private static List<string> ValidateRentPeriodSeeds(
            IReadOnlyCollection<RentPeriodSeedDto> periods,
            DateTimeOffset tenancyStart,
            DateTimeOffset? tenancyEnd,
            TenancyEndBehaviorEnum endBehavior)
        {
            return RentPeriodScheduleHelper.ValidateGeneratedSchedule(periods, tenancyStart, tenancyEnd, endBehavior);
        }

        private static List<RentReminderHistoryDto> MapReminderHistory(
            IEnumerable<RentReminder> reminders,
            IReadOnlyDictionary<string, string> requesterNames)
        {
            return reminders.Select(reminder => new RentReminderHistoryDto
            {
                Id = reminder.Id,
                TenancyId = reminder.TenancyId,
                Category = reminder.Category,
                CategoryLabel = reminder.Category switch
                {
                    RentReminderCategoryEnum.BeforeDue => "Before due date",
                    RentReminderCategoryEnum.DueDate => "Due date",
                    RentReminderCategoryEnum.AfterDue => "After due date",
                    RentReminderCategoryEnum.Manual => "Manual follow-up",
                    _ => "Rent reminder"
                },
                IsManual = reminder.IsManual,
                ScheduledFor = reminder.ScheduledFor,
                CreatedAt = reminder.CreatedAt,
                SentAt = reminder.SentAt,
                Status = reminder.Status,
                StatusLabel = reminder.Status switch
                {
                    RentReminderStatusEnum.Pending => "Pending",
                    RentReminderStatusEnum.Sent => "Sent",
                    RentReminderStatusEnum.PartiallySent => "Partially sent",
                    RentReminderStatusEnum.Failed => "Failed",
                    RentReminderStatusEnum.Cancelled => "Cancelled",
                    _ => reminder.Status.ToString()
                },
                EmailStatus = reminder.EmailStatus,
                SmsStatus = reminder.SmsStatus,
                RecipientEmail = reminder.RecipientEmail,
                RecipientPhone = reminder.RecipientPhone,
                RequestedByName = reminder.RequestedByUserId != null && requesterNames.TryGetValue(reminder.RequestedByUserId, out var name)
                    ? name
                    : string.Empty,
                FailureReason = reminder.FailureReason,
                OutstandingAmount = reminder.OutstandingAmountSnapshot,
                IncludedPeriodCount = reminder.IncludedPeriodCount,
                Periods = reminder.Periods
                    .OrderBy(period => period.PeriodStartSnapshot)
                    .Select(period => new RentReminderPeriodSnapshotDto
                    {
                        RentPeriodId = period.RentPeriodId,
                        PeriodStart = period.PeriodStartSnapshot,
                        PeriodEnd = period.PeriodEndSnapshot,
                        DueDate = period.DueDateSnapshot,
                        Amount = period.AmountSnapshot,
                        PaidAmount = period.PaidAmountSnapshot,
                        OutstandingAmount = period.OutstandingAmountSnapshot,
                        IsTrigger = period.IsTrigger,
                        IsUpcomingInformation = period.Relation == RentReminderPeriodRelationEnum.UpcomingInformation
                    })
                    .ToList()
            }).ToList();
        }

        private static RentSummaryDto BuildRentSummary(
            IReadOnlyCollection<RentPeriod> periods,
            IReadOnlyCollection<RentReminderHistoryDto> reminders,
            int manualReminderLimit,
            int manualCooldownHours,
            bool canSendManualReminder,
            DateTimeOffset nowUtc)
        {
            var paidPeriods = periods
                .Where(period => period.Status is RentPeriodStatusEnum.Paid
                    or RentPeriodStatusEnum.PaidBeforeRentHub
                    or RentPeriodStatusEnum.PaidInAdvance)
                .OrderByDescending(period => period.PeriodEnd)
                .ToList();
            var duePeriods = periods
                .Where(period => !RentPeriodScheduleHelper.IsPaidStatus(ResolveDisplayStatus(period, nowUtc)) &&
                                 period.DueDate <= nowUtc)
                .OrderBy(period => period.DueDate)
                .ToList();
            var nextPeriod = periods
                .Where(period => !RentPeriodScheduleHelper.IsPaidStatus(ResolveDisplayStatus(period, nowUtc)) &&
                                 period.DueDate > nowUtc)
                .OrderBy(period => period.DueDate)
                .FirstOrDefault();
            var successfulReminders = reminders
                .Where(reminder => reminder.Status is RentReminderStatusEnum.Sent or RentReminderStatusEnum.PartiallySent)
                .OrderByDescending(reminder => reminder.SentAt)
                .ToList();
            var oldestDuePeriodId = duePeriods.FirstOrDefault()?.Id;
            var manualRemindersForOldestPeriod = oldestDuePeriodId.HasValue
                ? successfulReminders.Count(reminder => reminder.IsManual &&
                    reminder.Periods.Any(period => period.RentPeriodId == oldestDuePeriodId.Value && period.IsTrigger))
                : 0;
            var lastManualSentAt = oldestDuePeriodId.HasValue
                ? successfulReminders
                    .Where(reminder => reminder.IsManual &&
                        reminder.Periods.Any(period => period.RentPeriodId == oldestDuePeriodId.Value && period.IsTrigger))
                    .Max(reminder => reminder.SentAt)
                : null;

            var unavailableReason = string.Empty;
            if (!canSendManualReminder)
                unavailableReason = "You do not have permission to send a rent reminder.";
            else if (!oldestDuePeriodId.HasValue)
                unavailableReason = "No unpaid rent is currently due.";
            else if (manualReminderLimit <= 0)
                unavailableReason = "Manual rent reminders are disabled for this apartment.";
            else if (manualRemindersForOldestPeriod >= manualReminderLimit)
                unavailableReason = "The manual reminder limit has been reached.";
            else if (lastManualSentAt.HasValue && manualCooldownHours > 0 &&
                     lastManualSentAt.Value.AddHours(manualCooldownHours) > nowUtc)
                unavailableReason = "The waiting period between manual reminders has not elapsed yet.";

            var lastPaid = paidPeriods.FirstOrDefault();
            var lastReminder = successfulReminders.FirstOrDefault();
            return new RentSummaryDto
            {
                DueNowAmount = duePeriods.Sum(period => Math.Max(0, period.Amount - period.PaidAmount)),
                DueNowPeriodCount = duePeriods.Count,
                OldestUnpaidDueDate = duePeriods.FirstOrDefault()?.DueDate,
                LastPaidPeriodStart = lastPaid?.PeriodStart,
                LastPaidPeriodEnd = lastPaid?.PeriodEnd,
                LastPaidAt = lastPaid?.PaidDate,
                NextPeriodStart = nextPeriod?.PeriodStart,
                NextPeriodEnd = nextPeriod?.PeriodEnd,
                NextPeriodDueDate = nextPeriod?.DueDate,
                NextPeriodAmount = nextPeriod?.Amount,
                LastReminderSentAt = lastReminder?.SentAt,
                LastReminderCategory = lastReminder?.Category,
                ReminderCount = successfulReminders.Count,
                ManualReminderCount = manualRemindersForOldestPeriod,
                ManualReminderLimit = manualReminderLimit,
                CanSendManualReminder = string.IsNullOrEmpty(unavailableReason),
                ManualReminderUnavailableReason = unavailableReason
            };
        }

        private static List<RentPeriodDto> MapRentPeriods(
            IEnumerable<RentPeriod> periods,
            DateTimeOffset nowUtc,
            IReadOnlyCollection<RentReminderHistoryDto>? reminderHistory = null)
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
                var periodReminders = reminderHistory?
                    .Where(reminder => reminder.Periods.Any(snapshot => snapshot.RentPeriodId == period.Id))
                    .OrderByDescending(reminder => reminder.SentAt ?? reminder.CreatedAt)
                    .ToList() ?? new List<RentReminderHistoryDto>();
                var successfulPeriodReminders = periodReminders
                    .Where(reminder => reminder.Status is RentReminderStatusEnum.Sent or RentReminderStatusEnum.PartiallySent)
                    .ToList();

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
                    CanCancelPendingPayment = status == RentPeriodStatusEnum.PendingPayment &&
                                              firstUnpaidId == period.Id &&
                                              period.PaymentId.HasValue,
                    LockedReason = isPaid || isPayable
                        ? string.Empty
                        : firstUnpaidId.HasValue
                            ? "Pay previous periods first"
                            : string.Empty,
                    ReminderCount = successfulPeriodReminders.Count,
                    ManualReminderCount = successfulPeriodReminders.Count(reminder => reminder.IsManual),
                    LastReminderSentAt = successfulPeriodReminders.Max(reminder => reminder.SentAt),
                    ReminderHistory = periodReminders
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

    }
}
