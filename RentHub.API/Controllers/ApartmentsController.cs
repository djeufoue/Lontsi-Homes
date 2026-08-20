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
using Common.Helpers;
using RentHub.API.Services.Permissions;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ApartmentsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IStorageService _storageService;
        private readonly IUserOnboardingService _userOnboardingService;
        private readonly IManagerPermissionService _permissionService;

        public ApartmentsController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IStorageService storageService,
            IUserOnboardingService userOnboardingService,
            IManagerPermissionService permissionService)
        {
            _context = context;
            _userManager = userManager;
            _storageService = storageService;
            _userOnboardingService = userOnboardingService;
            _permissionService = permissionService;
        }

        /// <summary>
        /// Lists all apartments that are currently vacant. Public listing.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetApartments()
        {
            try
            {
                var apartments = await _context.Apartments
                    .Include(a => a.Property)
                    .ThenInclude(p => p.Landlord)
                    .Include(a => a.Tenancies)
                    .Where(a => !a.IsDeleted)
                    .ToListAsync();

                var nowUtc = DateTimeOffset.UtcNow;
                var response = apartments
                    .Where(a => ApartmentStatusResolver.Resolve(a.Tenancies, nowUtc) == ApartmentStatusEnum.Vacant)
                    .Select(a => new ApartmentDto
                    {
                        Id = a.Id,
                        Name = a.Name,
                        Type = a.Type.ToString(),
                        Price = a.Price,
                        Area = a.Area,
                        PropertyName = a.Property != null ? a.Property.Name : string.Empty,
                        LandlordName = a.Property != null && a.Property.Landlord != null
                            ? (a.Property.Landlord.FullName ?? string.Empty)
                            : string.Empty,
                        Status = ApartmentStatusEnum.Vacant.ToString()
                    })
                    .ToList();

                return Ok(response);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Lists all apartments owned by current landlord.
        /// </summary>
        [HttpGet("mine")]
        [Authorize(Roles = "Landlord")]
        public async Task<IActionResult> GetMyApartments()
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var apartments = await _context.Apartments
                    .Include(a => a.Property)
                    .Include(a => a.Tenancies)
                    .Where(a => !a.IsDeleted && a.Property != null && a.Property.LandlordId == userId)
                    .OrderByDescending(a => a.CreatedAt)
                    .ToListAsync();

                var nowUtc = DateTimeOffset.UtcNow;
                var response = apartments.Select(a => new ApartmentDto
                    {
                        Id = a.Id,
                        Name = a.Name,
                        Type = a.Type.ToString(),
                        Price = a.Price,
                        Area = a.Area,
                        PropertyName = a.Property != null ? a.Property.Name : string.Empty,
                        LandlordName = string.Empty,
                        Status = ApartmentStatusResolver.Resolve(a.Tenancies, nowUtc).ToString()
                    })
                    .ToList();

                return Ok(response);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Apartment Overview DTO in ONE call (for MVC): Apartment + Tenancies + Owners + Documents.
        /// Access: landlord, property manager, apartment owner, tenant/member of a tenancy.
        /// </summary>
                [HttpGet("{id}/overview")]
        [Authorize]
        public async Task<IActionResult> GetApartmentOverview(int id)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                var isAdmin = User.IsInRole("Admin");
                var restrictToTenantAssignments = User.IsInRole("Tenant") &&
                                                  !User.IsInRole("Admin") &&
                                                  !User.IsInRole("Landlord") &&
                                                  !User.IsInRole("Manager");

                var apt = await _context.Apartments
                    .Include(a => a.Property)
                    .Include(a => a.RentReminderRules)
                    .FirstOrDefaultAsync(a => a.Id == id && !a.IsDeleted);

                if (apt == null) return NotFound("Apartment not found.");
                if (apt.Property == null) return NotFound("Property not found.");
                var isRestrictedManager = User.IsInRole("Manager") &&
                                          !isAdmin &&
                                          apt.Property.LandlordId != userId;

                var managerCanView = await _permissionService.HasApartmentPermissionAsync(
                    userId, id, ManagerPermission.ViewApartmentDetails, isAdmin);
                bool hasAccess =
                    isAdmin ||
                    apt.Property.LandlordId == userId ||
                    managerCanView ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        o.ApartmentId == id && o.OwnerId == userId && !o.IsDeleted) ||
                    await _context.Tenancies.AnyAsync(t =>
                        t.ApartmentId == id && !t.IsDeleted &&
                        t.Members.Any(mm => !mm.IsDeleted && mm.MemberId == userId));

                if (!hasAccess) return Forbid();

                var managerCanEdit = await _permissionService.HasApartmentPermissionAsync(
                    userId, id, ManagerPermission.EditApartment, isAdmin);
                bool canWrite =
                    isAdmin ||
                    apt.Property.LandlordId == userId ||
                    managerCanEdit ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        o.ApartmentId == id && o.OwnerId == userId && !o.IsDeleted && o.Permission == PermissionLevelEnum.ReadWrite);

                var canViewFinancialInformation = !isRestrictedManager || await _permissionService.HasApartmentPermissionAsync(
                    userId, id, ManagerPermission.ViewApartmentFinancialInformation, false);
                var canEditFinancialInformation = await _permissionService.HasApartmentPermissionAsync(
                    userId, id, ManagerPermission.EditPropertyFinancialInformation, isAdmin);
                var canViewMembers = !isRestrictedManager || await _permissionService.HasApartmentPermissionAsync(
                    userId, id, ManagerPermission.ViewMembers, false);
                var canManageMembers = await _permissionService.HasApartmentPermissionAsync(
                    userId, id, ManagerPermission.ManageApartmentMembers, isAdmin);
                var canViewDocuments = !isRestrictedManager || await _permissionService.HasApartmentPermissionAsync(
                    userId, id, ManagerPermission.ViewDocuments, false);
                var canManageDocuments = await _permissionService.HasApartmentPermissionAsync(
                    userId, id, ManagerPermission.ManageApartmentDocuments, isAdmin);
                var canAddTenancy = await _permissionService.HasApartmentPermissionAsync(
                    userId, id, ManagerPermission.AddTenancy, isAdmin);
                var canEditTenancy = await _permissionService.HasApartmentPermissionAsync(
                    userId, id, ManagerPermission.EditTenancy, isAdmin);
                var canSendRentReminder = await _permissionService.HasApartmentPermissionAsync(
                    userId, id, ManagerPermission.SendRentReminder, isAdmin);
                var canManageRentReminderSettings = await _permissionService.HasApartmentPermissionAsync(
                    userId, id, ManagerPermission.ManageRentReminderSettings, isAdmin) ||
                    await _context.ApartmentOwners.AnyAsync(owner =>
                        owner.ApartmentId == id &&
                        owner.OwnerId == userId &&
                        !owner.IsDeleted &&
                        owner.Permission == PermissionLevelEnum.ReadWrite);

                var canViewTenancies = await _permissionService.HasApartmentPermissionAsync(
                    userId, id, ManagerPermission.ViewTenancies, isAdmin);
                var tenancies = await _context.Tenancies
                    .Where(t =>
                        t.ApartmentId == id &&
                        !t.IsDeleted &&
                        (canViewTenancies || !User.IsInRole("Manager")) &&
                        (!restrictToTenantAssignments || t.Members.Any(member => !member.IsDeleted && member.MemberId == userId)))
                    .OrderByDescending(t => t.StartDate)
                    .ToListAsync();

                var tenancyPayments = canViewFinancialInformation
                    ? await _context.Payments
                        .Where(payment => payment.TenancyId != null && tenancies.Select(t => t.Id).Contains(payment.TenancyId.Value))
                        .ToListAsync()
                    : new List<Payment>();

                var tenancyRentPeriods = canViewFinancialInformation
                    ? await _context.RentPeriods
                        .Where(period => tenancies.Select(t => t.Id).Contains(period.TenancyId) && !period.IsDeleted)
                        .ToListAsync()
                    : new List<RentPeriod>();

                var tenancyIds = tenancies.Select(tenancy => tenancy.Id).ToList();
                var tenancyReminders = canViewFinancialInformation && tenancyIds.Count > 0
                    ? await _context.RentReminders
                        .AsNoTracking()
                        .Where(reminder => tenancyIds.Contains(reminder.TenancyId) &&
                                           (reminder.Status == RentReminderStatusEnum.Sent ||
                                            reminder.Status == RentReminderStatusEnum.PartiallySent))
                        .ToListAsync()
                    : new List<RentReminder>();

                var tenanciesDto = tenancies
                    .Select(tenancy =>
                    {
                        var dto = new TenancyDto
                        {
                            Id = tenancy.Id,
                            ApartmentName = apt.Name,
                            PropertyName = apt.Property!.Name,
                            StartDate = tenancy.StartDate,
                            EndDate = tenancy.EndDate,
                            MonthlyRent = canViewFinancialInformation ? tenancy.MonthlyRent : 0,
                            MaxMembers = tenancy.MaxMembers,
                            RentDueDay = tenancy.RentDueDay,
                            EndBehavior = tenancy.EndBehavior,
                            TerminatedAt = tenancy.TerminatedAt,
                            Status = ResolveTenancyStatus(tenancy, DateTimeOffset.UtcNow),
                            IsOwner = isAdmin || apt.Property!.LandlordId == userId
                        };

                        var periods = tenancyRentPeriods
                            .Where(period => period.TenancyId == tenancy.Id)
                            .OrderBy(period => period.PeriodStart)
                            .ToList();

                        if (canViewFinancialInformation && periods.Any())
                        {
                            return ApplyRentPeriodSnapshot(
                                dto,
                                periods,
                                apt,
                                tenancyReminders.Where(reminder => reminder.TenancyId == tenancy.Id).ToList(),
                                DateTimeOffset.UtcNow);
                        }

                        var snapshot = TenancyReminderHelpers.BuildSnapshot(
                            tenancy,
                            apt,
                            tenancyPayments.Where(payment => payment.TenancyId == tenancy.Id),
                            apt.RentReminderDaysBeforeDue,
                            apt.LeaseTerminationReminderDaysBeforeEnd,
                            DateTimeOffset.UtcNow);

                        return snapshot.ApplyTo(dto);
                    })
                    .ToList();

                var owners = restrictToTenantAssignments || !canViewMembers
                    ? new List<ApartmentOwnerDto>()
                    : await _context.ApartmentOwners
                        .Include(o => o.Owner)
                        .Where(o => o.ApartmentId == id && !o.IsDeleted)
                        .OrderByDescending(o => o.CreatedAt)
                        .Select(o => new ApartmentOwnerDto
                        {
                            Id = o.Id,
                            OwnerId = o.OwnerId,
                            OwnerName = o.Owner != null ? (o.Owner.FullName ?? o.Owner.Email ?? "") : "",
                            Role = o.Role,
                            Permission = o.Permission,
                            AssignedAt = o.CreatedAt
                        })
                        .ToListAsync();

                var documentEntities = canViewDocuments
                    ? await _context.Documents
                        .Where(d => d.ApartmentId == id && !d.IsDeleted)
                        .OrderByDescending(d => d.CreatedAt)
                        .ToListAsync()
                    : new List<Document>();

                var docs = await DocumentHelpers.ToDtosAsync(documentEntities, _storageService);

                var dto = new ApartmentOverviewDto
                {
                    Apartment = new ApartmentDetailsDto
                    {
                        Id = apt.Id,
                        PropertyId = apt.PropertyId,
                        PropertyName = apt.Property.Name,
                        Name = apt.Name,
                        Type = apt.Type.ToString(),
                        Price = canViewFinancialInformation ? apt.Price : 0,
                        Area = apt.Area,
                        Status = ApartmentStatusResolver.Resolve(tenancies, DateTimeOffset.UtcNow).ToString(),
                        RentReminderDaysBeforeDue = apt.RentReminderDaysBeforeDue,
                        LeaseTerminationReminderDaysBeforeEnd = apt.LeaseTerminationReminderDaysBeforeEnd,
                        ManualRentReminderLimit = apt.ManualRentReminderLimit,
                        ManualRentReminderCooldownHours = apt.ManualRentReminderCooldownHours,
                        RentReminderRules = apt.RentReminderRules
                            .OrderBy(rule => rule.SortOrder)
                            .Select(rule => new RentReminderRuleDto
                            {
                                Id = rule.Id,
                                Timing = rule.Timing,
                                Days = rule.Days,
                                IsEnabled = rule.IsEnabled,
                                EmailEnabled = rule.EmailEnabled,
                                SmsEnabled = rule.SmsEnabled,
                                SortOrder = rule.SortOrder
                            })
                            .ToList(),
                        CanWrite = canWrite
                    },
                    Tenancies = tenanciesDto,
                    Owners = owners,
                    Documents = docs,
                    CanViewFinancialInformation = canViewFinancialInformation,
                    CanEditFinancialInformation = canEditFinancialInformation,
                    CanManageMembers = canManageMembers,
                    CanManageDocuments = canManageDocuments,
                    CanAddTenancy = canAddTenancy,
                    CanEditTenancy = canEditTenancy,
                    CanSendRentReminder = canSendRentReminder,
                    CanManageRentReminderSettings = canManageRentReminderSettings
                };

                return Ok(dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Creates a new apartment under a property. Only landlord owner or manager RW can add.
        /// Checks name uniqueness (case-insensitive) within property.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> CreateApartment([FromBody] CreateApartmentRequest request)
        {
            try
            {
                var property = await _context.Properties.FirstOrDefaultAsync(p => p.Id == request.PropertyId);
                if (property == null) return NotFound("Property not found.");

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                bool canWrite = await _permissionService.HasPropertyPermissionAsync(
                    userId,
                    request.PropertyId,
                    ManagerPermission.AddApartment,
                    User.IsInRole("Admin"));

                if (!canWrite) return Forbid();

                if (!User.IsInRole("Admin") &&
                    !await PaymentAvailabilityHelper.HasActiveSubscriptionAsync(_context, property.LandlordId))
                {
                    return StatusCode(StatusCodes.Status402PaymentRequired, new
                    {
                        Code = "SUBSCRIPTION_PAYMENT_REQUIRED",
                        Message = PaymentAvailabilityHelper.SubscriptionRequiredMessage
                    });
                }

                var normalizedName = (request.Name ?? "").Trim();
                if (string.IsNullOrWhiteSpace(normalizedName))
                    return BadRequest("Apartment name is required.");

                var nameKey = normalizedName.ToUpper();
                var exists = await _context.Apartments.AnyAsync(a =>
                    a.PropertyId == request.PropertyId &&
                    !a.IsDeleted &&
                    a.Name != null &&
                    a.Name.Trim().ToUpper() == nameKey
                );

                if (exists)
                    return BadRequest($"An apartment with the name '{normalizedName}' already exists in this property.");

                var activeSubscription = await _context.UserSubscriptions
                    .Include(us => us.SubscriptionPlan)
                    .Where(us => us.UserId == property.LandlordId && !us.IsDeleted && us.IsApproved && us.PaymentStatus == PaymentStatusEnum.Success && us.EndDate > DateTimeOffset.UtcNow)
                    .OrderByDescending(us => us.EndDate)
                    .FirstOrDefaultAsync();

                var maxApts = activeSubscription != null
                    ? (activeSubscription.PlanMaxApartmentsPerPropertySnapshot ?? activeSubscription.SubscriptionPlan?.MaxApartmentsPerProperty)
                    : null;
                if (maxApts.HasValue)
                {
                    var count = await _context.Apartments.CountAsync(a => a.PropertyId == request.PropertyId && !a.IsDeleted);
                    if (count >= maxApts.Value)
                        return BadRequest($"Maximum number of apartments per property ({maxApts.Value}) reached for your subscription.");
                }

                var maxTotalApts = activeSubscription?.SubscriptionPlan?.MaxTotalApartments;
                if (maxTotalApts.HasValue)
                {
                    var totalCount = await _context.Apartments.CountAsync(a =>
                        a.Property != null &&
                        a.Property.LandlordId == property.LandlordId &&
                        !a.IsDeleted &&
                        !a.Property.IsDeleted);

                    if (totalCount >= maxTotalApts.Value)
                    {
                        return BadRequest($"Maximum total apartments ({maxTotalApts.Value}) reached for your subscription.");
                    }
                }

                var apt = new Apartment
                {
                    PropertyId = request.PropertyId,
                    Name = normalizedName,
                    Type = request.Type,
                    Price = request.Price,
                    Area = request.Area,
                    AdderId = userId,
                    Status = ApartmentStatusEnum.Vacant,
                    CreatedBy = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsDeleted = false,
                    RentReminderRules = new List<ApartmentRentReminderRule>
                    {
                        new()
                        {
                            Timing = RentReminderTimingEnum.BeforeDue,
                            Days = 10,
                            EmailEnabled = true,
                            SortOrder = 0,
                            CreatedBy = userId
                        },
                        new()
                        {
                            Timing = RentReminderTimingEnum.AfterDue,
                            Days = 5,
                            EmailEnabled = true,
                            SortOrder = 1,
                            CreatedBy = userId
                        }
                    }
                };

                _context.Apartments.Add(apt);
                await _context.SaveChangesAsync();

                var dto = new ApartmentDto
                {
                    Id = apt.Id,
                    Name = apt.Name,
                    Type = apt.Type.ToString(),
                    Price = apt.Price,
                    Area = apt.Area,
                    PropertyName = property.Name,
                    LandlordName = "",
                    Status = apt.Status.ToString()
                };

                return CreatedAtAction(nameof(GetApartmentOverview), new { id = apt.Id }, dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPut("{apartmentId}/reminder-settings")]
        [Authorize]
        public async Task<IActionResult> UpdateReminderSettings(int apartmentId, [FromBody] UpdateApartmentReminderSettingsRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var apartment = await _context.Apartments
                    .Include(a => a.Property)
                    .Include(a => a.RentReminderRules)
                    .FirstOrDefaultAsync(a => a.Id == apartmentId && !a.IsDeleted);

                if (apartment == null) return NotFound("Apartment not found.");
                if (apartment.Property == null) return NotFound("Property not found.");

                var canWrite =
                    await _permissionService.HasApartmentPermissionAsync(
                        userId,
                        apartmentId,
                        ManagerPermission.ManageRentReminderSettings,
                        User.IsInRole("Admin")) ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        o.ApartmentId == apartmentId &&
                        o.OwnerId == userId &&
                        !o.IsDeleted &&
                        o.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite) return Forbid();

                var requestedRules = request.RentReminderRules ?? new List<RentReminderRuleInputDto>
                {
                    new()
                    {
                        Timing = RentReminderTimingEnum.BeforeDue,
                        Days = request.RentReminderDaysBeforeDue,
                        IsEnabled = true,
                        EmailEnabled = true
                    },
                    new()
                    {
                        Timing = RentReminderTimingEnum.AfterDue,
                        Days = 5,
                        IsEnabled = true,
                        EmailEnabled = true
                    }
                };

                if (requestedRules.Count > 6)
                    return BadRequest("A maximum of 6 automatic rent reminder rules is allowed per apartment.");

                if (requestedRules.Any(rule => rule.Timing == RentReminderTimingEnum.OnDueDate && rule.Days != 0))
                    return BadRequest("A due-date reminder must use 0 days.");

                if (requestedRules.Any(rule => rule.Timing != RentReminderTimingEnum.OnDueDate && rule.Days == 0))
                    return BadRequest("Before-due and after-due reminders must use at least 1 day.");

                if (requestedRules.Any(rule => rule.IsEnabled && !rule.EmailEnabled && !rule.SmsEnabled))
                    return BadRequest("Each enabled reminder rule must use email or SMS.");

                if (requestedRules
                    .GroupBy(rule => new { rule.Timing, Days = rule.Timing == RentReminderTimingEnum.OnDueDate ? 0 : rule.Days })
                    .Any(group => group.Count() > 1))
                {
                    return BadRequest("Duplicate reminder rules are not allowed.");
                }

                var existingRulesById = apartment.RentReminderRules.ToDictionary(rule => rule.Id);
                var requestedIds = requestedRules
                    .Where(rule => rule.Id.HasValue)
                    .Select(rule => rule.Id!.Value)
                    .ToHashSet();

                if (requestedIds.Any(id => !existingRulesById.ContainsKey(id)))
                    return BadRequest("One or more reminder rules do not belong to this apartment.");

                var updatedAt = DateTimeOffset.UtcNow;
                await using var transaction = await _context.Database.BeginTransactionAsync();

                // Temporarily retire every current rule so timing/day combinations can be
                // safely changed or swapped without violating the filtered unique index.
                foreach (var existingRule in apartment.RentReminderRules)
                {
                    existingRule.IsDeleted = true;
                    existingRule.DeletedBy = userId;
                    existingRule.DeletedAt = updatedAt;
                }

                await _context.SaveChangesAsync();

                for (var index = 0; index < requestedRules.Count; index++)
                {
                    var input = requestedRules[index];
                    var normalizedDays = input.Timing == RentReminderTimingEnum.OnDueDate ? 0 : input.Days;
                    var rule = input.Id.HasValue
                        ? existingRulesById[input.Id.Value]
                        : apartment.RentReminderRules.FirstOrDefault(existing =>
                            existing.IsDeleted &&
                            existing.Timing == input.Timing &&
                            existing.Days == normalizedDays);
                    if (rule == null)
                    {
                        rule = new ApartmentRentReminderRule
                        {
                            ApartmentId = apartmentId,
                            CreatedBy = userId,
                            CreatedAt = updatedAt
                        };
                    }
                    else
                    {
                        rule.IsDeleted = false;
                        rule.DeletedBy = null;
                        rule.DeletedAt = null;
                    }

                    rule.Timing = input.Timing;
                    rule.Days = normalizedDays;
                    rule.IsEnabled = input.IsEnabled;
                    rule.EmailEnabled = input.EmailEnabled;
                    rule.SmsEnabled = input.SmsEnabled;
                    rule.SortOrder = index;
                    rule.UpdatedBy = userId;
                    rule.UpdatedAt = updatedAt;

                    if (rule.Id == 0)
                        _context.ApartmentRentReminderRules.Add(rule);
                }

                apartment.RentReminderDaysBeforeDue = requestedRules
                    .Where(rule => rule.IsEnabled && rule.Timing == RentReminderTimingEnum.BeforeDue)
                    .OrderByDescending(rule => rule.Days)
                    .Select(rule => rule.Days)
                    .FirstOrDefault();
                apartment.ManualRentReminderLimit = request.ManualRentReminderLimit;
                apartment.ManualRentReminderCooldownHours = request.ManualRentReminderCooldownHours;
                apartment.LeaseTerminationReminderDaysBeforeEnd = request.LeaseTerminationReminderDaysBeforeEnd;
                apartment.UpdatedBy = userId;
                apartment.UpdatedAt = updatedAt;

                _context.Apartments.Update(apartment);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new { Message = "Apartment reminder settings updated." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        // ---------------- Owners ----------------

        [HttpGet("{apartmentId}/owners")]
        [Authorize]
        public async Task<IActionResult> GetApartmentOwners(int apartmentId)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var apt = await _context.Apartments
                    .Include(a => a.Property)
                    .FirstOrDefaultAsync(a => a.Id == apartmentId && !a.IsDeleted);

                if (apt == null) return NotFound("Apartment not found.");
                if (apt.Property == null) return NotFound("Property not found.");

                bool hasAccess =
                    apt.Property.LandlordId == userId ||
                    await _permissionService.HasApartmentPermissionAsync(userId, apartmentId, ManagerPermission.ViewMembers, User.IsInRole("Admin")) ||
                    await _context.ApartmentOwners.AnyAsync(o => o.ApartmentId == apartmentId && o.OwnerId == userId && !o.IsDeleted);

                if (!hasAccess) return Forbid();

                var owners = await _context.ApartmentOwners
                    .Include(o => o.Owner)
                    .Where(o => o.ApartmentId == apartmentId && !o.IsDeleted)
                    .OrderByDescending(o => o.CreatedAt)
                    .Select(o => new ApartmentOwnerDto
                    {
                        Id = o.Id,
                        OwnerId = o.OwnerId,
                        OwnerName = o.Owner != null ? (o.Owner.FullName ?? o.Owner.Email ?? "") : "",
                        Role = o.Role,
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

        [HttpPost("{apartmentId}/owners")]
        [Authorize]
        public async Task<IActionResult> AssignOwner(int apartmentId, [FromBody] AssignApartmentOwnerRequest request)
        {
            try
            {
                if (!ModelState.IsValid) return BadRequest(ModelState);

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var apt = await _context.Apartments
                    .Include(a => a.Property)
                    .FirstOrDefaultAsync(a => a.Id == apartmentId && !a.IsDeleted);

                if (apt == null) return NotFound("Apartment not found.");
                if (apt.Property == null) return NotFound("Property not found.");

                bool canWrite =
                    await _permissionService.HasApartmentPermissionAsync(
                        userId, apartmentId, ManagerPermission.ManageApartmentMembers, User.IsInRole("Admin"));

                if (!canWrite) return Forbid();

                if (!User.IsInRole("Admin") &&
                    !await PaymentAvailabilityHelper.HasActiveSubscriptionAsync(_context, apt.Property.LandlordId))
                {
                    return StatusCode(StatusCodes.Status402PaymentRequired, new
                    {
                        Code = "SUBSCRIPTION_REQUIRED",
                        Message = PaymentAvailabilityHelper.SubscriptionRequiredMessage
                    });
                }

                var email = (request.Email ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(email)) return BadRequest("Email is required.");

                var requestedRoleName = request.Role == ApartmentMemberRoleEnum.Owner ? "Owner" : "Manager";
                var ownerUser = (await _userOnboardingService.EnsureUserAsync(
                    email,
                    request.FullName,
                    request.CountryCode,
                    request.PhoneNumber,
                    null,
                    requestedRoleName)).User;

                var already = await _context.ApartmentOwners.AnyAsync(o =>
                    o.ApartmentId == apartmentId && o.OwnerId == ownerUser.Id && !o.IsDeleted);

                if (already) return BadRequest("This member is already assigned to the apartment.");

                var isPropertyMember = await _context.PropertyManagerAssignments.AnyAsync(m =>
                    m.PropertyId == apt.PropertyId && m.ManagerId == ownerUser.Id && !m.IsDeleted);

                if (isPropertyMember)
                    return BadRequest("Property members cannot also be added as apartment or tenancy members within the same property.");

                var isTenancyMemberOnApartment = await _context.TenancyMembers.AnyAsync(m =>
                    !m.IsDeleted &&
                    m.MemberId == ownerUser.Id &&
                    m.Tenancy != null &&
                    !m.Tenancy.IsDeleted &&
                    m.Tenancy.ApartmentId == apartmentId);

                if (isTenancyMemberOnApartment)
                    return BadRequest("A tenancy member cannot also be added as an apartment member for the same apartment.");

                var entity = new ApartmentOwner
                {
                    ApartmentId = apartmentId,
                    OwnerId = ownerUser.Id,
                    Role = request.Role,
                    Permission = request.Permission,
                    CreatedBy = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsDeleted = false
                };

                _context.ApartmentOwners.Add(entity);
                await _context.SaveChangesAsync();

                var dto = new ApartmentOwnerDto
                {
                    Id = entity.Id,
                    OwnerId = entity.OwnerId,
                    OwnerName = ownerUser.FullName ?? ownerUser.Email ?? "",
                    Role = entity.Role,
                    Permission = entity.Permission,
                    AssignedAt = entity.CreatedAt
                };

                return Ok(dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPut("{apartmentId}/owners/{assignmentId}")]
        [Authorize]
        public async Task<IActionResult> UpdateOwnerPermission(int apartmentId, int assignmentId, [FromBody] UpdateApartmentMemberRequest request)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var assignment = await _context.ApartmentOwners
                    .Include(o => o.Apartment)
                    .ThenInclude(a => a.Property)
                    .FirstOrDefaultAsync(o => o.Id == assignmentId && o.ApartmentId == apartmentId);

                if (assignment == null) return NotFound("Owner assignment not found.");
                if (assignment.Apartment?.Property == null) return NotFound("Property not found.");

                bool canWrite =
                    await _permissionService.HasApartmentPermissionAsync(
                        userId, apartmentId, ManagerPermission.ManageApartmentMembers, User.IsInRole("Admin"));

                if (!canWrite) return Forbid();

                assignment.Role = request.Role;
                assignment.Permission = request.Permission;
                assignment.UpdatedBy = userId;
                assignment.UpdatedAt = DateTimeOffset.UtcNow;

                var assignedUser = await _userManager.FindByIdAsync(assignment.OwnerId);
                if (assignedUser != null)
                {
                    var requestedRoleName = request.Role == ApartmentMemberRoleEnum.Owner ? "Owner" : "Manager";
                    if (!await _userManager.IsInRoleAsync(assignedUser, requestedRoleName))
                    {
                        await _userManager.AddToRoleAsync(assignedUser, requestedRoleName);
                    }
                }

                _context.ApartmentOwners.Update(assignment);
                await _context.SaveChangesAsync();

                return Ok(new { Message = "Apartment member updated." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpDelete("{apartmentId}/owners/{assignmentId}")]
        [Authorize]
        public async Task<IActionResult> RemoveOwner(int apartmentId, int assignmentId)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var assignment = await _context.ApartmentOwners
                    .Include(o => o.Apartment)
                    .ThenInclude(a => a.Property)
                    .FirstOrDefaultAsync(o => o.Id == assignmentId && o.ApartmentId == apartmentId);

                if (assignment == null) return NotFound("Owner assignment not found.");
                if (assignment.Apartment?.Property == null) return NotFound("Property not found.");

                bool canWrite =
                    await _permissionService.HasApartmentPermissionAsync(
                        userId, apartmentId, ManagerPermission.ManageApartmentMembers, User.IsInRole("Admin"));

                if (!canWrite) return Forbid();

                assignment.IsDeleted = true;
                assignment.UpdatedBy = userId;
                assignment.UpdatedAt = DateTimeOffset.UtcNow;

                _context.ApartmentOwners.Update(assignment);
                await _context.SaveChangesAsync();

                return Ok(new { Message = "Owner removed." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        private static TenancyDto ApplyRentPeriodSnapshot(
            TenancyDto dto,
            IReadOnlyList<RentPeriod> periods,
            Apartment apartment,
            IReadOnlyList<RentReminder> reminders,
            DateTimeOffset nowUtc)
        {
            var lastPaid = periods
                .Where(period => period.Status is RentPeriodStatusEnum.Paid
                    or RentPeriodStatusEnum.PaidBeforeRentHub
                    or RentPeriodStatusEnum.PaidInAdvance)
                .OrderByDescending(period => period.PeriodEnd)
                .FirstOrDefault();

            var nextUnpaid = periods
                .OrderBy(period => period.PeriodStart)
                .FirstOrDefault(period => !RentPeriodScheduleHelper.IsPaidStatus(period.Status));
            var duePeriods = periods
                .Where(period => !RentPeriodScheduleHelper.IsPaidStatus(period.Status) && period.DueDate <= nowUtc)
                .OrderBy(period => period.DueDate)
                .ToList();
            var nextUpcoming = periods
                .Where(period => !RentPeriodScheduleHelper.IsPaidStatus(period.Status) && period.DueDate > nowUtc)
                .OrderBy(period => period.DueDate)
                .FirstOrDefault();
            var lastReminder = reminders
                .OrderByDescending(reminder => reminder.SentAt)
                .FirstOrDefault();

            dto.PaidThroughDate = lastPaid?.PeriodEnd;
            dto.NextRentDueDate = nextUnpaid?.DueDate;
            dto.NextRentReminderDate = nextUnpaid?.DueDate.AddDays(-Math.Max(0, apartment.RentReminderDaysBeforeDue));
            dto.LeaseTerminationReminderDate = dto.EndDate?.AddDays(-Math.Max(0, apartment.LeaseTerminationReminderDaysBeforeEnd));
            dto.IsPaidInAdvance = nextUnpaid != null && nextUnpaid.DueDate.Date > nowUtc.Date;
            dto.LastPaidPeriodStart = lastPaid?.PeriodStart;
            dto.LastPaidPeriodEnd = lastPaid?.PeriodEnd;
            dto.LastPaidAt = lastPaid?.PaidDate;
            dto.DueNowAmount = duePeriods.Sum(period => Math.Max(0, period.Amount - period.PaidAmount));
            dto.DueNowPeriodCount = duePeriods.Count;
            dto.OldestUnpaidDueDate = duePeriods.FirstOrDefault()?.DueDate;
            dto.NextUpcomingPeriodStart = nextUpcoming?.PeriodStart;
            dto.NextUpcomingPeriodEnd = nextUpcoming?.PeriodEnd;
            dto.NextUpcomingAmount = nextUpcoming?.Amount;
            dto.LastReminderSentAt = lastReminder?.SentAt;
            dto.LastReminderCategory = lastReminder?.Category;
            dto.ReminderCount = reminders.Count;
            return dto;
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












