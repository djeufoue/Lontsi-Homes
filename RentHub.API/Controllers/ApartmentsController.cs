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
    public class ApartmentsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IStorageService _storageService;
        private readonly IUserOnboardingService _userOnboardingService;

        public ApartmentsController(
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
                    .Where(a => !a.IsDeleted && a.Status == ApartmentStatusEnum.Vacant)
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
                        Status = a.Status.ToString()
                    })
                    .ToListAsync();

                return Ok(apartments);
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
                    .Where(a => !a.IsDeleted && a.Property != null && a.Property.LandlordId == userId)
                    .OrderByDescending(a => a.CreatedAt)
                    .Select(a => new ApartmentDto
                    {
                        Id = a.Id,
                        Name = a.Name,
                        Type = a.Type.ToString(),
                        Price = a.Price,
                        Area = a.Area,
                        PropertyName = a.Property != null ? a.Property.Name : string.Empty,
                        LandlordName = string.Empty,
                        Status = a.Status.ToString()
                    })
                    .ToListAsync();

                return Ok(apartments);
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

                var apt = await _context.Apartments
                    .Include(a => a.Property)
                    .FirstOrDefaultAsync(a => a.Id == id && !a.IsDeleted);

                if (apt == null) return NotFound("Apartment not found.");
                if (apt.Property == null) return NotFound("Property not found.");

                bool hasAccess =
                    isAdmin ||
                    apt.Property.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m =>
                        m.PropertyId == apt.PropertyId && m.ManagerId == userId && !m.IsDeleted) ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        o.ApartmentId == id && o.OwnerId == userId && !o.IsDeleted) ||
                    await _context.Tenancies.AnyAsync(t =>
                        t.ApartmentId == id && !t.IsDeleted &&
                        t.Members.Any(mm => !mm.IsDeleted && mm.MemberId == userId));

                if (!hasAccess) return Forbid();

                bool canWrite =
                    isAdmin ||
                    apt.Property.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m =>
                        m.PropertyId == apt.PropertyId && m.ManagerId == userId && !m.IsDeleted && m.Permission == PermissionLevelEnum.ReadWrite) ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        o.ApartmentId == id && o.OwnerId == userId && !o.IsDeleted && o.Permission == PermissionLevelEnum.ReadWrite);

                var tenancies = await _context.Tenancies
                    .Where(t => t.ApartmentId == id && !t.IsDeleted)
                    .OrderByDescending(t => t.StartDate)
                    .ToListAsync();

                var tenancyPayments = await _context.Payments
                    .Where(payment => payment.TenancyId != null && tenancies.Select(t => t.Id).Contains(payment.TenancyId.Value))
                    .ToListAsync();

                var tenanciesDto = tenancies
                    .Select(tenancy =>
                    {
                        var snapshot = TenancyReminderHelpers.BuildSnapshot(
                            tenancy,
                            apt,
                            tenancyPayments.Where(payment => payment.TenancyId == tenancy.Id),
                            apt.RentReminderDaysBeforeDue,
                            apt.LeaseTerminationReminderDaysBeforeEnd,
                            DateTimeOffset.UtcNow);

                        return snapshot.ApplyTo(new TenancyDto
                        {
                            Id = tenancy.Id,
                            ApartmentName = apt.Name,
                            PropertyName = apt.Property!.Name,
                            StartDate = tenancy.StartDate,
                            EndDate = tenancy.EndDate,
                            MonthlyRent = tenancy.MonthlyRent,
                            MaxMembers = tenancy.MaxMembers,
                            IsOwner = isAdmin || apt.Property!.LandlordId == userId
                        });
                    })
                    .ToList();

                var owners = await _context.ApartmentOwners
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

                var documentEntities = await _context.Documents
                    .Where(d => d.ApartmentId == id && !d.IsDeleted)
                    .OrderByDescending(d => d.CreatedAt)
                    .ToListAsync();

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
                        Price = apt.Price,
                        Area = apt.Area,
                        Status = apt.Status.ToString(),
                        RentReminderDaysBeforeDue = apt.RentReminderDaysBeforeDue,
                        LeaseTerminationReminderDaysBeforeEnd = apt.LeaseTerminationReminderDaysBeforeEnd,
                        CanWrite = canWrite
                    },
                    Tenancies = tenanciesDto,
                    Owners = owners,
                    Documents = docs
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

                bool canWrite =
                    property.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m =>
                        m.PropertyId == request.PropertyId &&
                        m.ManagerId == userId &&
                        !m.IsDeleted &&
                        m.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite) return Forbid();

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
                    .Where(us => us.UserId == property.LandlordId && !us.IsDeleted && us.EndDate > DateTimeOffset.UtcNow)
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
                    IsDeleted = false
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
                    .FirstOrDefaultAsync(a => a.Id == apartmentId && !a.IsDeleted);

                if (apartment == null) return NotFound("Apartment not found.");
                if (apartment.Property == null) return NotFound("Property not found.");

                var canWrite =
                    apartment.Property.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m =>
                        m.PropertyId == apartment.PropertyId &&
                        m.ManagerId == userId &&
                        !m.IsDeleted &&
                        m.Permission == PermissionLevelEnum.ReadWrite) ||
                    await _context.ApartmentOwners.AnyAsync(o =>
                        o.ApartmentId == apartmentId &&
                        o.OwnerId == userId &&
                        !o.IsDeleted &&
                        o.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite) return Forbid();

                apartment.RentReminderDaysBeforeDue = request.RentReminderDaysBeforeDue;
                apartment.LeaseTerminationReminderDaysBeforeEnd = request.LeaseTerminationReminderDaysBeforeEnd;
                apartment.UpdatedBy = userId;
                apartment.UpdatedAt = DateTimeOffset.UtcNow;

                _context.Apartments.Update(apartment);
                await _context.SaveChangesAsync();

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
                    await _context.PropertyManagerAssignments.AnyAsync(m => m.PropertyId == apt.PropertyId && m.ManagerId == userId && !m.IsDeleted) ||
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
                    apt.Property.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m =>
                        m.PropertyId == apt.PropertyId &&
                        m.ManagerId == userId &&
                        !m.IsDeleted &&
                        m.Permission == PermissionLevelEnum.ReadWrite);

                if (!canWrite) return Forbid();

                var email = (request.Email ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(email)) return BadRequest("Email is required.");

                var requestedRoleName = request.Role == ApartmentMemberRoleEnum.Owner ? "Owner" : "Manager";
                var ownerUser = (await _userOnboardingService.EnsureUserAsync(
                    email,
                    request.FullName,
                    request.CountryCode,
                    request.PhoneNumber,
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
                    assignment.Apartment.Property.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m =>
                        m.PropertyId == assignment.Apartment.PropertyId &&
                        m.ManagerId == userId &&
                        !m.IsDeleted &&
                        m.Permission == PermissionLevelEnum.ReadWrite);

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
                    assignment.Apartment.Property.LandlordId == userId ||
                    await _context.PropertyManagerAssignments.AnyAsync(m =>
                        m.PropertyId == assignment.Apartment.PropertyId &&
                        m.ManagerId == userId &&
                        !m.IsDeleted &&
                        m.Permission == PermissionLevelEnum.ReadWrite);

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
    }
}












