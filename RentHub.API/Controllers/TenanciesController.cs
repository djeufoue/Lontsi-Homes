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
using RentHub.API.Services.Tenancies;

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
        private readonly ITenancyTerminationEmailService _terminationEmailService;

        public TenanciesController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IStorageService storageService,
            IUserOnboardingService userOnboardingService,
            IManagerPermissionService permissionService,
            IRentReminderService rentReminderService,
            ITenancyTerminationEmailService terminationEmailService)
        {
            _context = context;
            _userManager = userManager;
            _storageService = storageService;
            _userOnboardingService = userOnboardingService;
            _permissionService = permissionService;
            _rentReminderService = rentReminderService;
            _terminationEmailService = terminationEmailService;
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
                        PaymentIntervalMonths = t.PaymentIntervalMonths,
                        EndBehavior = t.EndBehavior,
                        FutureRentPeriodCount = t.FutureRentPeriodCount,
                        RentTrackingStartDate = t.RentTrackingStartDate,
                        RentScheduleNeedsReview = t.RentScheduleNeedsReview,
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

                var normalizedEndBehavior = NormalizeEndBehavior(request.EndBehavior);
                var normalizedEndDate = normalizedEndBehavior == TenancyEndBehaviorEnum.NoEndDate ? null : request.EndDate;

                if (normalizedEndDate.HasValue && normalizedEndDate.Value < request.StartDate)
                    return BadRequest("EndDate cannot be earlier than StartDate.");

                if (request.MaxMembers <= 0)
                    return BadRequest("MaxMembers must be greater than 0.");

                if (request.RentDueDay is < 1 or > 31)
                    return BadRequest("RentDueDay must be between 1 and 31.");

                if (request.FutureRentPeriodCount is < 1 or > 12)
                    return BadRequest("FutureRentPeriodCount must be between 1 and 12.");

                if (request.PaymentIntervalMonths is < 1 or > 12)
                    return BadRequest("PaymentIntervalMonths must be between 1 and 12.");

                if (normalizedEndBehavior == TenancyEndBehaviorEnum.ExpireAutomatically && !normalizedEndDate.HasValue)
                    return BadRequest("EndDate is required when the tenancy should expire automatically.");

                var hasOverlap = await TenancyLifecycleHelper.HasOverlappingTenancyAsync(_context, request.ApartmentId, request.StartDate, normalizedEndDate);
                if (hasOverlap)
                    return BadRequest("This apartment already has a tenancy that overlaps with the selected period.");

                var tenancy = new Tenancy
                {
                    ApartmentId = request.ApartmentId,
                    StartDate = request.StartDate,
                    EndDate = normalizedEndDate,
                    MonthlyRent = request.MonthlyRent,
                    MaxMembers = request.MaxMembers,
                    RentDueDay = request.RentDueDay,
                    PaymentIntervalMonths = request.PaymentIntervalMonths,
                    EndBehavior = normalizedEndBehavior,
                    FutureRentPeriodCount = request.FutureRentPeriodCount,
                    RentTrackingStartDate = request.RentTrackingStartDate ?? request.StartDate,
                    CreatedBy = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsDeleted = false
                };

                _context.Tenancies.Add(tenancy);
                await _context.SaveChangesAsync();

                var generatedPeriods = RentPeriodScheduleHelper.GeneratePeriods(
                    tenancy.StartDate,
                    tenancy.EndDate,
                    tenancy.EndBehavior,
                    tenancy.MonthlyRent,
                    tenancy.RentDueDay,
                    DateTimeOffset.UtcNow,
                    tenancy.FutureRentPeriodCount,
                    tenancy.PaymentIntervalMonths,
                    tenancy.RentTrackingStartDate);
                _context.RentPeriods.AddRange(generatedPeriods.Select(period => new RentPeriod
                {
                    TenancyId = tenancy.Id,
                    PeriodStart = period.PeriodStart,
                    PeriodEnd = period.PeriodEnd,
                    DueDate = period.DueDate,
                    BillingGroupSequence = period.BillingGroupSequence,
                    Amount = period.Amount,
                    PaidAmount = 0,
                    Status = period.Status,
                    CreatedBy = userId,
                    CreatedAt = DateTimeOffset.UtcNow
                }));
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
                    PaymentIntervalMonths = tenancy.PaymentIntervalMonths,
                    EndBehavior = tenancy.EndBehavior,
                    FutureRentPeriodCount = tenancy.FutureRentPeriodCount,
                    RentTrackingStartDate = tenancy.RentTrackingStartDate,
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

                var normalizedEndBehavior = NormalizeEndBehavior(request.EndBehavior);
                var normalizedEndDate = normalizedEndBehavior == TenancyEndBehaviorEnum.NoEndDate ? null : request.EndDate;

                if (request.FutureRentPeriodCount is < 1 or > 12)
                    return BadRequest("Future rent-period count must be between 1 and 12.");

                if (request.PaymentIntervalMonths is < 1 or > 12)
                    return BadRequest("Payment interval must be between 1 and 12 months.");

                if (normalizedEndBehavior == TenancyEndBehaviorEnum.ExpireAutomatically && !normalizedEndDate.HasValue)
                    return BadRequest("End date is required when the tenancy should expire automatically.");

                if (normalizedEndDate.HasValue && normalizedEndDate.Value < request.StartDate)
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

                var hasOverlap = await TenancyLifecycleHelper.HasOverlappingTenancyAsync(_context, request.ApartmentId, request.StartDate, normalizedEndDate);
                if (hasOverlap)
                    return BadRequest("This apartment already has a tenancy that overlaps with the selected period.");

                var trackingStart = request.RentTrackingStartDate == default
                    ? request.RentPeriods.OrderBy(period => period.PeriodStart).First().PeriodStart
                    : request.RentTrackingStartDate;
                var periodValidationErrors = ValidateRentPeriodSeeds(
                    request.RentPeriods,
                    request.StartDate,
                    normalizedEndDate,
                    normalizedEndBehavior,
                    request.PaymentIntervalMonths,
                    trackingStart);
                if (periodValidationErrors.Count > 0)
                    return BadRequest(new { Message = string.Join(" ", periodValidationErrors) });

                var tenancy = new Tenancy
                {
                    ApartmentId = request.ApartmentId,
                    StartDate = request.StartDate,
                    EndDate = normalizedEndDate,
                    MonthlyRent = request.MonthlyRent,
                    MaxMembers = request.MaxMembers,
                    RentDueDay = request.RentDueDay,
                    PaymentIntervalMonths = request.PaymentIntervalMonths,
                    EndBehavior = normalizedEndBehavior,
                    FutureRentPeriodCount = request.FutureRentPeriodCount,
                    RentTrackingStartDate = trackingStart,
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
                        BillingGroupSequence = period.BillingGroupSequence,
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
                    PaymentIntervalMonths = tenancy.PaymentIntervalMonths,
                    EndBehavior = tenancy.EndBehavior,
                    FutureRentPeriodCount = tenancy.FutureRentPeriodCount,
                    RentTrackingStartDate = tenancy.RentTrackingStartDate,
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

                var canEditFinancialInformation = apartmentOwnerCanWrite ||
                    await _permissionService.HasApartmentPermissionAsync(
                        userId, tenancy.ApartmentId, ManagerPermission.EditPropertyFinancialInformation, isAdmin);

                if (canEditFinancialInformation && request.MonthlyRent <= 0)
                    return BadRequest("MonthlyRent must be greater than 0.");

                if (!canEditFinancialInformation &&
                    (request.MonthlyRent != tenancy.MonthlyRent ||
                     request.RentDueDay != tenancy.RentDueDay ||
                     request.PaymentIntervalMonths != tenancy.PaymentIntervalMonths ||
                     request.FutureRentPeriodCount != tenancy.FutureRentPeriodCount))
                    return Forbid();

                var normalizedEndBehavior = NormalizeEndBehavior(request.EndBehavior);
                var normalizedEndDate = normalizedEndBehavior == TenancyEndBehaviorEnum.NoEndDate ? null : request.EndDate;

                if (normalizedEndDate.HasValue && normalizedEndDate.Value < request.StartDate)
                    return BadRequest("EndDate cannot be earlier than StartDate.");

                if (request.MaxMembers <= 0)
                    return BadRequest("MaxMembers must be greater than 0.");

                if (request.RentDueDay is < 1 or > 31)
                    return BadRequest("RentDueDay must be between 1 and 31.");

                if (request.FutureRentPeriodCount is < 1 or > 12)
                    return BadRequest("FutureRentPeriodCount must be between 1 and 12.");

                if (request.PaymentIntervalMonths is < 1 or > 12)
                    return BadRequest("PaymentIntervalMonths must be between 1 and 12.");

                if (normalizedEndBehavior == TenancyEndBehaviorEnum.ExpireAutomatically && !normalizedEndDate.HasValue)
                    return BadRequest("EndDate is required when the tenancy should expire automatically.");

                var hasOverlap = await TenancyLifecycleHelper.HasOverlappingTenancyAsync(
                    _context,
                    tenancy.ApartmentId,
                    request.StartDate,
                    normalizedEndDate,
                    tenancy.Id);

                if (hasOverlap)
                    return BadRequest("This apartment already has a tenancy that overlaps with the selected period.");

                var scheduleChanged = tenancy.StartDate != request.StartDate ||
                    tenancy.EndDate != normalizedEndDate ||
                    tenancy.EndBehavior != normalizedEndBehavior ||
                    tenancy.MonthlyRent != request.MonthlyRent ||
                    tenancy.RentDueDay != request.RentDueDay ||
                    tenancy.PaymentIntervalMonths != request.PaymentIntervalMonths ||
                    tenancy.FutureRentPeriodCount != request.FutureRentPeriodCount ||
                    (request.RentTrackingStartDate != default &&
                     tenancy.RentTrackingStartDate != request.RentTrackingStartDate);

                tenancy.StartDate = request.StartDate;
                tenancy.EndDate = normalizedEndDate;
                if (canEditFinancialInformation)
                {
                    tenancy.MonthlyRent = request.MonthlyRent;
                }
                tenancy.MaxMembers = request.MaxMembers;
                tenancy.RentDueDay = request.RentDueDay;
                tenancy.PaymentIntervalMonths = request.PaymentIntervalMonths;
                tenancy.EndBehavior = normalizedEndBehavior;
                tenancy.FutureRentPeriodCount = request.FutureRentPeriodCount;
                tenancy.RentTrackingStartDate = request.RentTrackingStartDate == default
                    ? tenancy.RentTrackingStartDate
                    : request.RentTrackingStartDate;
                tenancy.UpdatedBy = userId;
                tenancy.UpdatedAt = DateTimeOffset.UtcNow;

                await using var transaction = await _context.Database.BeginTransactionAsync();
                if (scheduleChanged)
                {
                    await TenancyLifecycleHelper.ReconcileRentScheduleAsync(
                        _context,
                        tenancy,
                        userId,
                        DateTimeOffset.UtcNow);
                    await TenancyLifecycleHelper.InvalidateRentRemindersAsync(
                        _context,
                        tenancy.Id,
                        DateTimeOffset.UtcNow,
                        "The tenancy rent schedule changed after this reminder was planned.");
                }
                _context.Tenancies.Update(tenancy);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new { Message = "Tenancy updated successfully." });
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { Message = ex.Message });
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
                        PaymentIntervalMonths = t.PaymentIntervalMonths,
                        EndBehavior = t.EndBehavior,
                        FutureRentPeriodCount = t.FutureRentPeriodCount,
                        RentTrackingStartDate = t.RentTrackingStartDate,
                        RentScheduleNeedsReview = t.RentScheduleNeedsReview,
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
                var hasManagerAssignment = await _context.PropertyManagerAssignments.AnyAsync(assignment =>
                    !assignment.IsDeleted &&
                    assignment.PropertyId == tenancy.Apartment.PropertyId &&
                    assignment.ManagerId == userId);
                var isRestrictedManager = hasManagerAssignment &&
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
                var hasApartmentOwnership = await _context.ApartmentOwners.AnyAsync(assignment =>
                    !assignment.IsDeleted &&
                    assignment.ApartmentId == tenancy.ApartmentId &&
                    assignment.OwnerId == userId);
                var canViewRentReminderHistory = canViewRent &&
                                                   (isAdmin ||
                                                    tenancy.Apartment.Property.LandlordId == userId ||
                                                    hasManagerAssignment ||
                                                    hasApartmentOwnership);
                var isPropertyLandlord = tenancy.Apartment.Property.LandlordId == userId;
                var canEditMemberEmails = isPropertyLandlord ||
                    (hasManagerAssignment && await _permissionService.HasPropertyPermissionAsync(
                        userId,
                        tenancy.Apartment.PropertyId,
                        ManagerPermission.EditMember,
                        false));
                var canWrite = canEdit || canDelete || canTerminate || canRenew || canAddMembers || canEditMembers ||
                               canRemoveMembers || canUploadDocuments || canDeleteDocuments || canMarkRentPaid ||
                               canCancelPendingPayment || canSendRentReminder;
                var requestingMembership = tenancy.Members.FirstOrDefault(member =>
                    !member.IsDeleted && member.MemberId == userId);
                var isRequestingTenant = requestingMembership?.Role is TenancyMemberRoleEnum.MainTenant or TenancyMemberRoleEnum.CoTenant;
                var hasPendingTerminationRequest = isRequestingTenant &&
                    await _context.TenancyTerminationRequests.AnyAsync(request =>
                        request.TenancyId == tenancy.Id &&
                        request.Status == TenancyTerminationRequestStatusEnum.Pending);
                var hasPendingRenewalRequest = isRequestingTenant &&
                    await _context.TenancyExtensionRequests.AnyAsync(request =>
                        request.TenancyId == tenancy.Id &&
                        request.Status == TenancyExtensionStatusEnum.Pending);

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

                var reminderEntities = canViewRentReminderHistory
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
                var deletableHistoricalPeriods = isPropertyLandlord
                    ? rentPeriods.Where(period =>
                        period.Status == RentPeriodStatusEnum.PaidBeforeRentHub &&
                        !period.PaymentId.HasValue &&
                        string.IsNullOrWhiteSpace(period.PaymentReference))
                        .OrderBy(period => period.PeriodStart)
                        .ToList()
                    : new List<RentPeriod>();
                var deletableHistoricalPeriodCount = deletableHistoricalPeriods.Count;
                var nowUtc = DateTimeOffset.UtcNow;
                var rentSummary = BuildRentSummary(
                    rentPeriods,
                    reminderHistory,
                    tenancy.Apartment.ManualRentReminderLimit,
                    tenancy.Apartment.ManualRentReminderCooldownHours,
                    canSendRentReminder &&
                    (!tenancy.TerminatedAt.HasValue || tenancy.TerminatedAt.Value.Date > nowUtc.Date),
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
                        LeaseTerminationReminderDate = tenancy.EndDate?.AddDays(
                            -Math.Max(0, tenancy.Apartment.LeaseTerminationReminderDaysBeforeEnd)),
                        MonthlyRent = tenancy.MonthlyRent,
                        MaxMembers = tenancy.MaxMembers,
                        RentDueDay = tenancy.RentDueDay,
                        PaymentIntervalMonths = tenancy.PaymentIntervalMonths,
                        EndBehavior = tenancy.EndBehavior,
                        FutureRentPeriodCount = tenancy.FutureRentPeriodCount,
                        RentTrackingStartDate = tenancy.RentTrackingStartDate,
                        RentScheduleNeedsReview = tenancy.RentScheduleNeedsReview,
                        CanCorrectRentSchedule = isPropertyLandlord && tenancy.RentScheduleNeedsReview,
                        TerminatedAt = tenancy.TerminatedAt,
                        TerminationReason = tenancy.TerminationReason,
                        TerminationNotes = tenancy.TerminationNotes,
                        Status = ResolveTenancyStatus(tenancy, nowUtc),
                        CanWrite = canWrite,
                        CanEdit = canEdit,
                        CanDelete = canDelete,
                        CanTerminate = canTerminate,
                        CanRenew = canRenew,
                        CanRequestTermination = isRequestingTenant &&
                                                !tenancy.TerminatedAt.HasValue &&
                                                !hasPendingTerminationRequest &&
                                                !hasPendingRenewalRequest &&
                                                (!tenancy.EndDate.HasValue || tenancy.EndDate.Value.Date > nowUtc.Date),
                        CanRequestRenewal = isRequestingTenant &&
                                            !tenancy.TerminatedAt.HasValue &&
                                            !hasPendingTerminationRequest &&
                                            !hasPendingRenewalRequest &&
                                            tenancy.EndDate.HasValue &&
                                            tenancy.EndDate.Value.Date >= nowUtc.Date,
                        CanViewMembers = canViewMembers,
                        CanAddMembers = canAddMembers,
                        CanEditMembers = canEditMembers,
                        CanEditMemberEmails = canEditMemberEmails,
                        CanRemoveMembers = canRemoveMembers,
                        CanViewDocuments = canViewDocuments,
                        CanUploadDocuments = canUploadDocuments,
                        CanDeleteDocuments = canDeleteDocuments,
                        CanViewRent = canViewRent,
                        CanViewRentReminderHistory = canViewRentReminderHistory,
                        CanMarkRentPaid = canMarkRentPaid,
                        CanCancelPendingPayment = canCancelPendingPayment,
                        CanSendRentReminder = canSendRentReminder,
                        CanDeleteHistoricalRentPeriods = deletableHistoricalPeriodCount > 0,
                        DeletableHistoricalRentPeriodCount = deletableHistoricalPeriodCount,
                        DeletableHistoricalFirstPeriodStart = deletableHistoricalPeriods.FirstOrDefault()?.PeriodStart,
                        DeletableHistoricalLastPeriodEnd = deletableHistoricalPeriods.LastOrDefault()?.PeriodEnd
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

        [HttpPost("{id}/rent-periods/archive-paid-before-platform")]
        [Authorize]
        public async Task<IActionResult> ArchivePaidBeforePlatformPeriods(int id)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            var tenancy = await _context.Tenancies
                .Include(item => item.Apartment)
                .ThenInclude(apartment => apartment!.Property)
                .FirstOrDefaultAsync(item => item.Id == id && !item.IsDeleted);
            if (tenancy?.Apartment?.Property == null) return NotFound("Tenancy not found.");

            // This intentionally does not grant an administrator or manager override.
            // The cleanup is reserved to the landlord who owns the property.
            if (tenancy.Apartment.Property.LandlordId != userId) return Forbid();

            await using var transaction = await _context.Database.BeginTransactionAsync();
            var periods = await _context.RentPeriods
                .Where(period => period.TenancyId == id && !period.IsDeleted)
                .OrderBy(period => period.PeriodStart)
                .ToListAsync();
            var eligible = periods
                .Where(period => period.Status == RentPeriodStatusEnum.PaidBeforeRentHub &&
                                 !period.PaymentId.HasValue &&
                                 string.IsNullOrWhiteSpace(period.PaymentReference))
                .ToList();
            if (eligible.Count == 0)
                return Conflict("There are no historical paid periods eligible for archival.");

            var nowUtc = DateTimeOffset.UtcNow;
            foreach (var period in eligible)
            {
                period.IsDeleted = true;
                period.DeletedBy = userId;
                period.DeletedAt = nowUtc;
                period.UpdatedBy = userId;
                period.UpdatedAt = nowUtc;
            }

            var firstRemainingStart = periods
                .Where(period => !eligible.Contains(period))
                .Select(period => (DateTimeOffset?)period.PeriodStart)
                .FirstOrDefault();
            tenancy.RentTrackingStartDate = firstRemainingStart ??
                RentPeriodScheduleHelper.ResolveNextBillingGroupStart(
                    tenancy.StartDate,
                    nowUtc,
                    tenancy.PaymentIntervalMonths);
            tenancy.UpdatedBy = userId;
            tenancy.UpdatedAt = nowUtc;

            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
            return Ok(new
            {
                ArchivedPeriodCount = eligible.Count,
                FirstPeriodStart = eligible.First().PeriodStart,
                LastPeriodEnd = eligible.Last().PeriodEnd,
                tenancy.RentTrackingStartDate
            });
        }

        [HttpPost("{id}/rent-schedule/reconcile")]
        [Authorize]
        public async Task<IActionResult> ReconcileLegacyRentSchedule(
            int id,
            [FromBody] ReconcileRentScheduleRequest request)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
            if (request.RentDueDay is < 1 or > 31)
                return BadRequest("RentDueDay must be between 1 and 31.");
            if (request.PaymentIntervalMonths is < 1 or > 12)
                return BadRequest("PaymentIntervalMonths must be between 1 and 12.");

            var tenancy = await _context.Tenancies
                .Include(item => item.Apartment)
                .ThenInclude(apartment => apartment!.Property)
                .FirstOrDefaultAsync(item => item.Id == id && !item.IsDeleted);
            if (tenancy?.Apartment?.Property == null) return NotFound("Tenancy not found.");
            if (tenancy.Apartment.Property.LandlordId != userId) return Forbid();
            if (!tenancy.RentScheduleNeedsReview)
                return Conflict("This rent schedule is not marked as requiring legacy correction.");
            if (request.FirstTrackedPeriodStart.Date < tenancy.StartDate.Date ||
                !RentPeriodScheduleHelper.IsMonthlyBoundary(tenancy.StartDate, request.FirstTrackedPeriodStart))
            {
                return BadRequest("The first tracked period must be a monthly boundary calculated from the tenancy start date.");
            }
            if (tenancy.EndDate.HasValue && request.FirstTrackedPeriodStart.Date > tenancy.EndDate.Value.Date)
                return BadRequest("The first tracked period cannot start after the tenancy end date.");

            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var nowUtc = DateTimeOffset.UtcNow;
                tenancy.RentDueDay = request.RentDueDay;
                tenancy.PaymentIntervalMonths = request.PaymentIntervalMonths;
                tenancy.RentTrackingStartDate = request.FirstTrackedPeriodStart;
                await TenancyLifecycleHelper.ReconcileRentScheduleAsync(
                    _context,
                    tenancy,
                    userId,
                    nowUtc);
                await TenancyLifecycleHelper.InvalidateRentRemindersAsync(
                    _context,
                    tenancy.Id,
                    nowUtc,
                    "Sent using an old rent schedule — invalidated after correction.");
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
                return Ok(new { Message = "Rent schedule corrected successfully." });
            }
            catch (InvalidOperationException ex)
            {
                await transaction.RollbackAsync();
                return Conflict(new { Message = ex.Message });
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
                                LeaseTerminationReminderDate = tenancy.EndDate?.AddDays(
                                    -Math.Max(0, tenancy.Apartment?.LeaseTerminationReminderDaysBeforeEnd ?? 30)),
                                MonthlyRent = tenancy.MonthlyRent,
                                MaxMembers = tenancy.MaxMembers,
                                RentDueDay = tenancy.RentDueDay,
                                PaymentIntervalMonths = tenancy.PaymentIntervalMonths,
                                EndBehavior = tenancy.EndBehavior,
                                FutureRentPeriodCount = tenancy.FutureRentPeriodCount,
                                RentTrackingStartDate = tenancy.RentTrackingStartDate,
                                RentScheduleNeedsReview = tenancy.RentScheduleNeedsReview,
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

                var terminationDate = new DateTimeOffset(
                    request.TerminationDate.Year,
                    request.TerminationDate.Month,
                    request.TerminationDate.Day,
                    0,
                    0,
                    0,
                    TimeSpan.Zero);
                var nowUtc = DateTimeOffset.UtcNow;
                var minimumNoticeDate = CameroonTenancyNoticePolicy.MinimumLandlordTerminationDate(nowUtc);
                if (terminationDate.Date < minimumNoticeDate.Date)
                    return BadRequest($"A landlord termination date must provide at least three months' notice. Choose {minimumNoticeDate:yyyy-MM-dd} or later.");
                if (terminationDate < tenancy.StartDate)
                    return BadRequest("Termination date cannot be before the tenancy start date.");
                if (tenancy.EndDate.HasValue && terminationDate.Date > tenancy.EndDate.Value.Date)
                    return BadRequest("The termination date cannot be later than the current fixed end date.");
                if (tenancy.TerminatedAt.HasValue)
                    return Conflict(new { Message = "This tenancy already has an end decision." });
                if (await _context.TenancyTerminationRequests.AnyAsync(item =>
                        item.TenancyId == id && item.Status == TenancyTerminationRequestStatusEnum.Pending) ||
                    await _context.TenancyExtensionRequests.AnyAsync(item =>
                        item.TenancyId == id && item.Status == TenancyExtensionStatusEnum.Pending))
                {
                    return Conflict(new { Message = "Review the pending tenancy request before recording a direct termination decision." });
                }

                TenancyTerminationRequest decision;
                var originalEndDate = tenancy.EndDate;
                await using (var transaction = await _context.Database.BeginTransactionAsync())
                {
                    await TenancyLifecycleHelper.ApplyTerminationAsync(
                        _context,
                        tenancy,
                        terminationDate,
                        userId,
                        request.Reason,
                        request.Notes,
                        nowUtc);

                    var actor = await _userManager.FindByIdAsync(userId);
                    decision = new TenancyTerminationRequest
                    {
                        TenancyId = tenancy.Id,
                        Tenancy = tenancy,
                        RequestedById = userId,
                        RequestedBy = actor,
                        OriginalEndDate = originalEndDate,
                        RequestedEndDate = terminationDate,
                        Reason = string.IsNullOrWhiteSpace(request.Notes)
                            ? request.Reason.ToString()
                            : $"{request.Reason}: {request.Notes.Trim()}",
                        Status = TenancyTerminationRequestStatusEnum.Approved,
                        ReviewedById = userId,
                        ReviewedBy = actor,
                        ReviewedAt = nowUtc,
                        CreatedBy = userId,
                        CreatedAt = nowUtc,
                        UpdatedBy = userId,
                        UpdatedAt = nowUtc
                    };
                    _context.TenancyTerminationRequests.Add(decision);
                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                }

                try
                {
                    await _terminationEmailService.SendDirectDecisionAsync(decision);
                }
                catch (Exception emailException)
                {
                    // The legal/business decision is durable even if notification delivery is temporarily unavailable.
                    HttpContext.RequestServices
                        .GetRequiredService<ILogger<TenanciesController>>()
                        .LogError(emailException, "Direct tenancy termination {RequestId} was saved but email delivery failed.", decision.Id);
                }

                return Ok(new { Message = "Tenancy termination decision recorded.", RequestId = decision.Id });
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

            return string.Equals(PhoneNumberHelper.Normalize(landlord.CountryCode), "+237", StringComparison.OrdinalIgnoreCase);
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

                return string.Equals(PhoneNumberHelper.Normalize(propertyCountryCode), "+237", StringComparison.OrdinalIgnoreCase);
            }

            return IsCameroonProfile(landlord);
        }

        private static List<string> ValidateRentPeriodSeeds(
            IReadOnlyCollection<RentPeriodSeedDto> periods,
            DateTimeOffset tenancyStart,
            DateTimeOffset? tenancyEnd,
            TenancyEndBehaviorEnum endBehavior,
            int paymentIntervalMonths,
            DateTimeOffset trackingStartDate)
        {
            return RentPeriodScheduleHelper.ValidateGeneratedSchedule(
                periods,
                tenancyStart,
                tenancyEnd,
                endBehavior,
                paymentIntervalMonths,
                trackingStartDate);
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
                StatusLabel = reminder.InvalidatedAt.HasValue
                    ? (string.IsNullOrWhiteSpace(reminder.InvalidationReason)
                        ? "Invalidated reminder"
                        : reminder.InvalidationReason)
                    : reminder.Status switch
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
                InvalidatedAt = reminder.InvalidatedAt,
                InvalidationReason = reminder.InvalidationReason,
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
                .Where(reminder => !reminder.InvalidatedAt.HasValue &&
                    reminder.Status is RentReminderStatusEnum.Sent or RentReminderStatusEnum.PartiallySent)
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
                    .Where(reminder => reminder.Periods.Any(snapshot =>
                        snapshot.RentPeriodId == period.Id
                        && snapshot.IsTrigger
                        && !snapshot.IsUpcomingInformation))
                    .OrderByDescending(reminder => reminder.SentAt ?? reminder.CreatedAt)
                    .ToList() ?? new List<RentReminderHistoryDto>();
                var successfulPeriodReminders = periodReminders
                    .Where(reminder => !reminder.InvalidatedAt.HasValue &&
                        reminder.Status is RentReminderStatusEnum.Sent or RentReminderStatusEnum.PartiallySent)
                    .ToList();

                return new RentPeriodDto
                {
                    Id = period.Id,
                    TenancyId = period.TenancyId,
                    PeriodStart = period.PeriodStart,
                    PeriodEnd = period.PeriodEnd,
                    DueDate = period.DueDate,
                    BillingGroupSequence = period.BillingGroupSequence,
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

        private static TenancyEndBehaviorEnum NormalizeEndBehavior(TenancyEndBehaviorEnum endBehavior)
        {
            return endBehavior == TenancyEndBehaviorEnum.ContinueMonthToMonth
                ? TenancyEndBehaviorEnum.NoEndDate
                : endBehavior;
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
            if (tenancy.TerminatedAt.HasValue && tenancy.TerminatedAt.Value.Date < nowUtc.Date)
            {
                return "Terminated";
            }

            if (tenancy.StartDate.Date > nowUtc.Date)
            {
                return "Upcoming";
            }

            if (tenancy.TerminatedAt.HasValue)
            {
                return "Ending";
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
