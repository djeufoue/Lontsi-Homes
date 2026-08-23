using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using RentHub.API.Data;
using RentHub.API.Models.Entities;
using RentHub.API.Helpers;
using Common.CommunicationModels;
using Common.Enums;
using RentHub.API.Services.Storage;
using RentHub.API.Services.Permissions;
using RentHub.API.Services.Maps;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PropertiesController : ControllerBase
    {
        private const int MinDialogAutoCloseSeconds = 2;
        private const int MaxDialogAutoCloseSeconds = 30;
        private const string DefaultDialogPosition = "bottom-center";
        private static readonly HashSet<string> AllowedDialogPositions = new(StringComparer.OrdinalIgnoreCase)
        {
            "top-start",
            "top-center",
            "top-end",
            "center-start",
            "center",
            "center-end",
            "bottom-start",
            DefaultDialogPosition,
            "bottom-end"
        };

        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IStorageService _storageService;
        private readonly IConfiguration _configuration;
        private readonly IManagerPermissionService _permissionService;
        private readonly IPropertyGeocodingService _geocodingService;

        public PropertiesController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IStorageService storageService,
            IConfiguration configuration,
            IManagerPermissionService permissionService,
            IPropertyGeocodingService geocodingService)
        {
            _context = context;
            _userManager = userManager;
            _storageService = storageService;
            _configuration = configuration;
            _permissionService = permissionService;
            _geocodingService = geocodingService;
        }
        /// <summary>
        /// Admin-only full property list endpoint.
        /// </summary>
        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetProperties()
        {
            try
            {
                var properties = await _context.Properties
                    .Include(p => p.Apartments)
                    .Include(p => p.Landlord)
                    .OrderByDescending(p => p.CreatedAt)
                    .Select(p => new PropertyDto
                    {
                        Id = p.Id,
                        Name = p.Name,
                        City = p.City,
                        Address = p.Address,
                        CountryCode = p.CountryCode,
                        CountryIsoCode = p.CountryIsoCode,
                        ApartmentCount = p.Apartments.Count(a => !a.IsDeleted),
                        LandlordId = p.LandlordId,
                        LandlordName = p.Landlord != null ? (p.Landlord.FullName ?? p.Landlord.Email ?? "") : "",
                        CanWrite = true,
                        AccessSource = "Admin"
                    })
                    .ToListAsync();

                return Ok(properties);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Role-aware property listing for the dashboard with filtering and pagination.
        /// </summary>
        [HttpGet("dashboard")]
        [Authorize]
        public async Task<IActionResult> GetDashboardProperties(
            [FromQuery] string? search = null,
            [FromQuery] string? city = null,
            [FromQuery] string? access = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 12)
        {
            try
            {
                page = page < 1 ? 1 : page;
                pageSize = pageSize < 1 ? 12 : Math.Min(pageSize, 50);

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var user = await _userManager.FindByIdAsync(userId);
                if (user == null) return Unauthorized();

                var roles = (await _userManager.GetRolesAsync(user)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var isAdmin = roles.Contains("Admin");
                var isLandlord = roles.Contains("Landlord");
                var isManager = roles.Contains("Manager");

                var managed = await _context.PropertyManagerAssignments
                    .Where(m => !m.IsDeleted && m.ManagerId == userId)
                    .Select(m => new { m.PropertyId, m.PermissionFlags })
                    .ToListAsync();

                var managedAll = managed
                    .Where(m => (m.PermissionFlags & (long)ManagerPermission.ViewProperty) != 0)
                    .Select(m => m.PropertyId)
                    .ToHashSet();
                var managedRw = managed
                    .Where(m => (m.PermissionFlags & (long)ManagerPermission.EditProperty) != 0)
                    .Select(m => m.PropertyId)
                    .ToHashSet();

                var ownerPropertyIds = await _context.ApartmentOwners
                    .Where(o => !o.IsDeleted && o.OwnerId == userId)
                    .Select(o => o.Apartment!.PropertyId)
                    .Distinct()
                    .ToListAsync();
                var ownerProps = ownerPropertyIds.ToHashSet();

                var tenantPropertyIds = await _context.Tenancies
                    .Where(t => !t.IsDeleted && t.Members.Any(mm => !mm.IsDeleted && mm.MemberId == userId))
                    .Select(t => t.Apartment!.PropertyId)
                    .Distinct()
                    .ToListAsync();
                var tenantProps = tenantPropertyIds.ToHashSet();

                var query = _context.Properties
                    .Include(p => p.Apartments)
                    .Include(p => p.Landlord)
                    .Where(p => !p.IsDeleted)
                    .AsQueryable();

                if (!isAdmin)
                {
                    query = query.Where(p =>
                        p.LandlordId == userId ||
                        managedAll.Contains(p.Id) ||
                        ownerProps.Contains(p.Id) ||
                        tenantProps.Contains(p.Id));
                }

                var accessFilter = (access ?? string.Empty).Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(accessFilter) && !isAdmin)
                {
                    query = accessFilter switch
                    {
                        "owned" => query.Where(p => p.LandlordId == userId),
                        "managed" => query.Where(p => managedAll.Contains(p.Id)),
                        "owner" => query.Where(p => ownerProps.Contains(p.Id)),
                        "tenant" => query.Where(p => tenantProps.Contains(p.Id)),
                        _ => query
                    };
                }

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var s = search.Trim().ToLower();
                    query = query.Where(p =>
                        p.Name.ToLower().Contains(s) ||
                        p.City.ToLower().Contains(s) ||
                        p.Address.ToLower().Contains(s) ||
                        (p.CountryIsoCode != null && p.CountryIsoCode.ToLower().Contains(s)) ||
                        (p.CountryCode != null && p.CountryCode.ToLower().Contains(s)));
                }

                if (!string.IsNullOrWhiteSpace(city))
                {
                    var c = city.Trim().ToLower();
                    query = query.Where(p => p.City.ToLower().Contains(c));
                }

                var totalCount = await query.CountAsync();

                var properties = await query
                    .OrderByDescending(p => p.CreatedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                var items = properties.Select(p => new PropertyDto
                {
                    Id = p.Id,
                    Name = p.Name,
                    City = p.City,
                    Address = p.Address,
                    CountryCode = p.CountryCode,
                    CountryIsoCode = p.CountryIsoCode,
                    ApartmentCount = p.Apartments.Count(a => !a.IsDeleted),
                    LandlordId = p.LandlordId,
                    LandlordName = p.Landlord != null ? (p.Landlord.FullName ?? p.Landlord.Email ?? "") : "",
                    CanWrite = isAdmin || p.LandlordId == userId || managedRw.Contains(p.Id),
                    AccessSource = PropertyHelpers.ResolveAccessSource(isAdmin, p.LandlordId == userId, managedAll.Contains(p.Id), ownerProps.Contains(p.Id), tenantProps.Contains(p.Id)),
                    AutomaticPaymentsEnabled = p.AutomaticPaymentsEnabled
                }).ToList();

                var response = new PropertyListResponseDto
                {
                    Items = items,
                    Page = page,
                    PageSize = pageSize,
                    TotalCount = totalCount,
                    UserRole = PropertyHelpers.ResolvePrimaryRole(roles),
                    CanCreateProperty = false
                };

                if (isAdmin)
                {
                    response.CanCreateProperty = true;
                    response.CreationScopes.Add(new PropertyCreationScopeDto
                    {
                        LandlordId = userId,
                        LandlordName = user.FullName ?? user.Email ?? "Admin",
                        CurrentProperties = await _context.Properties.CountAsync(),
                        MaxProperties = null,
                        SubscriptionApproved = true,
                        CanCreate = true,
                        StatusMessage = "Administrator can create properties."
                    });
                }

                if (isLandlord)
                {
                    var scope = await PropertyHelpers.BuildCreationScopeAsync(
                        _context,
                        userId,
                        user.FullName ?? user.Email ?? "Landlord",
                        IsStripePayoutSetupRequired());
                    response.CreationScopes.Add(scope);
                    response.CanCreateProperty = response.CanCreateProperty || scope.CanCreate;
                }

                if (isManager)
                {
                    var managerLandlords = await _context.PropertyManagerAssignments
                        .Where(m => !m.IsDeleted && m.ManagerId == userId &&
                                    (m.PermissionFlags & (long)ManagerPermission.AddProperty) != 0)
                        .Join(_context.Properties.Include(p => p.Landlord), m => m.PropertyId, p => p.Id, (m, p) => new
                        {
                            p.LandlordId,
                            LandlordName = p.Landlord != null ? (p.Landlord.FullName ?? p.Landlord.Email ?? "") : "Landlord"
                        })
                        .Distinct()
                        .ToListAsync();

                    foreach (var landlord in managerLandlords)
                    {
                        var scope = await PropertyHelpers.BuildCreationScopeAsync(
                            _context,
                            landlord.LandlordId,
                            landlord.LandlordName,
                            IsStripePayoutSetupRequired());
                        response.CreationScopes.Add(scope);
                        response.CanCreateProperty = response.CanCreateProperty || scope.CanCreate;
                    }
                }

                response.CreationScopes = response.CreationScopes
                    .GroupBy(s => s.LandlordId)
                    .Select(g => g.First())
                    .ToList();

                return Ok(response);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Backward-compatible landlord endpoint.
        /// </summary>
        [HttpGet("mine")]
        [Authorize]
        public async Task<IActionResult> GetMyProperties()
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var props = await _context.Properties
                    .Include(p => p.Apartments)
                    .Include(p => p.Landlord)
                    .Where(p => p.LandlordId == userId)
                    .OrderByDescending(p => p.CreatedAt)
                    .Select(p => new PropertyDto
                    {
                        Id = p.Id,
                        Name = p.Name,
                        City = p.City,
                        Address = p.Address,
                        CountryCode = p.CountryCode,
                        CountryIsoCode = p.CountryIsoCode,
                        ApartmentCount = p.Apartments.Count(a => !a.IsDeleted),
                        LandlordId = p.LandlordId,
                        LandlordName = p.Landlord != null ? (p.Landlord.FullName ?? p.Landlord.Email ?? "") : "",
                        CanWrite = true,
                        AccessSource = "Owned",
                        AutomaticPaymentsEnabled = p.AutomaticPaymentsEnabled
                    })
                    .ToListAsync();

                return Ok(props);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Returns details of a specific property.
        /// </summary>
        [HttpGet("{id}")]
        [Authorize]
        public async Task<IActionResult> GetProperty(int id)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var user = await _userManager.FindByIdAsync(userId);
                if (user == null) return Unauthorized();

                var roles = (await _userManager.GetRolesAsync(user)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var isAdmin = roles.Contains("Admin");

                var property = await _context.Properties
                    .Include(p => p.Apartments)
                    .ThenInclude(a => a.Tenancies)
                    .ThenInclude(tenancy => tenancy.Members)
                    .Include(p => p.Landlord)
                    .FirstOrDefaultAsync(p => p.Id == id);

                if (property == null) return NotFound("Property not found.");

                var hasManagerAssignment = await _context.PropertyManagerAssignments.AnyAsync(assignment =>
                    !assignment.IsDeleted && assignment.PropertyId == id && assignment.ManagerId == userId);
                var hasApartmentOwnerAssignment = await _context.ApartmentOwners.AnyAsync(assignment =>
                    !assignment.IsDeleted &&
                    assignment.OwnerId == userId &&
                    assignment.Apartment != null &&
                    assignment.Apartment.PropertyId == id);
                var hasTenantAssignment = property.Apartments.Any(apartment =>
                    !apartment.IsDeleted &&
                    apartment.Tenancies.Any(tenancy =>
                        !tenancy.IsDeleted &&
                        tenancy.Members.Any(member => !member.IsDeleted && member.MemberId == userId)));
                var restrictToTenantAssignments = roles.Contains("Tenant") &&
                                                  !isAdmin &&
                                                  property.LandlordId != userId &&
                                                  !hasManagerAssignment &&
                                                  !hasApartmentOwnerAssignment;

                var hasAccess = await PropertyHelpers.CanAccessPropertyAsync(_context, id, userId, isAdmin);
                if (!hasAccess) return Forbid();
                if (hasManagerAssignment && !hasTenantAssignment && !hasApartmentOwnerAssignment &&
                    !isAdmin && property.LandlordId != userId &&
                    !await _permissionService.HasPropertyPermissionAsync(userId, id, ManagerPermission.ViewProperty, false))
                {
                    return Forbid();
                }

                var managerApartmentIds = hasManagerAssignment && !isAdmin && property.LandlordId != userId
                    ? await _permissionService.GetAccessibleApartmentIdsAsync(userId, id, ManagerPermission.ViewApartments, false)
                    : null;

                var dto = new PropertyDetailDto
                {
                    Id = property.Id,
                    Name = property.Name,
                    City = property.City,
                    Address = property.Address,
                    CountryCode = property.CountryCode,
                    CountryIsoCode = property.CountryIsoCode,
                    Description = property.Description,
                    Latitude = property.Latitude,
                    Longitude = property.Longitude,
                    MapEnabled = property.MapEnabled,
                    SuccessDialogShowSuccessMessages = property.SuccessDialogShowSuccessMessages,
                    SuccessDialogAutoCloseEnabled = property.SuccessDialogAutoCloseEnabled,
                    SuccessDialogAutoCloseSeconds = property.SuccessDialogAutoCloseSeconds,
                    SuccessDialogPosition = property.SuccessDialogPosition,
                    LandlordId = property.LandlordId,
                    LandlordName = property.Landlord != null ? (property.Landlord.FullName ?? property.Landlord.Email ?? "") : "",
                    Apartments = property.Apartments
                        .Where(a =>
                            !a.IsDeleted &&
                            (managerApartmentIds == null || managerApartmentIds.Contains(a.Id)) &&
                            (!restrictToTenantAssignments || a.Tenancies.Any(tenancy =>
                                !tenancy.IsDeleted && tenancy.Members.Any(member => !member.IsDeleted && member.MemberId == userId))))
                        .Select(a => new ApartmentDto
                        {
                            Id = a.Id,
                            Name = a.Name,
                            Type = a.Type.ToString(),
                            Price = a.Price,
                            Area = a.Area,
                            PropertyName = property.Name,
                            LandlordName = property.Landlord != null ? (property.Landlord.FullName ?? string.Empty) : string.Empty,
                            Status = ApartmentStatusResolver.Resolve(a.Tenancies, DateTimeOffset.UtcNow).ToString()
                        })
                        .ToList()
                };

                return Ok(dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Creates a new property.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> CreateProperty([FromBody] CreatePropertyRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId))
                    return Unauthorized();

                var user = await _userManager.FindByIdAsync(userId);
                if (user == null)
                    return Unauthorized();

                var roles = (await _userManager.GetRolesAsync(user)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var isAdmin = roles.Contains("Admin");
                var isLandlord = roles.Contains("Landlord");
                var isManager = roles.Contains("Manager");

                string landlordId;
                if (isAdmin)
                {
                    landlordId = string.IsNullOrWhiteSpace(request.LandlordId) ? userId : request.LandlordId!.Trim();
                }
                else if (isLandlord)
                {
                    landlordId = userId;
                }
                else if (isManager)
                {
                    if (string.IsNullOrWhiteSpace(request.LandlordId))
                        return BadRequest("Manager must select the landlord to create property for.");

                    landlordId = request.LandlordId.Trim();

                    var canCreateForLandlord = await _context.PropertyManagerAssignments
                        .Where(m => m.ManagerId == userId &&
                                    (m.PermissionFlags & (long)ManagerPermission.AddProperty) != 0)
                        .Join(_context.Properties, m => m.PropertyId, p => p.Id, (m, p) => p.LandlordId)
                        .AnyAsync(id => id == landlordId);

                    if (!canCreateForLandlord)
                        return Forbid();
                }
                else
                {
                    return Forbid();
                }

                var landlord = await _userManager.FindByIdAsync(landlordId);
                if (landlord == null)
                    return BadRequest("Target landlord account was not found.");

                if (!isAdmin)
                {
                    var creationScope = await PropertyHelpers.BuildCreationScopeAsync(
                        _context,
                        landlordId,
                        landlord.FullName ?? landlord.Email ?? "Landlord",
                        IsStripePayoutSetupRequired());
                    if (!creationScope.CanCreate)
                        return BadRequest(creationScope.StatusMessage);
                }

                var normalizedName = request.Name.Trim();
                if (string.IsNullOrWhiteSpace(normalizedName))
                    return BadRequest("Property name is required.");

                var normalizedCity = request.City.Trim();
                var normalizedAddress = request.Address.Trim();

                if (string.IsNullOrWhiteSpace(normalizedCity) || string.IsNullOrWhiteSpace(normalizedAddress))
                    return BadRequest("City and Address are required.");
                if (request.Latitude.HasValue != request.Longitude.HasValue)
                    return BadRequest("Latitude and longitude must be provided together.");

                var country = ResolvePropertyCountry(
                    request.CountryIsoCode,
                    request.CountryCode,
                    null,
                    null,
                    landlord.CountryIsoCode,
                    landlord.CountryCode,
                    requireCountry: true);
                if (country.CountryIsoCode == null || country.CountryCode == null)
                    return BadRequest("Property country is required.");

                var nameKey = normalizedName.ToUpperInvariant();
                var cityKey = normalizedCity.ToUpperInvariant();

                var propertyExists = await _context.Properties.AnyAsync(p =>
                    p.LandlordId == landlordId &&
                    p.Name.Trim().ToUpper() == nameKey &&
                    p.City.Trim().ToUpper() == cityKey);

                if (propertyExists)
                    return BadRequest($"A property named '{normalizedName}' already exists for this landlord in {normalizedCity}.");

                var resolvedCoordinates = request.Latitude.HasValue
                    ? null
                    : await _geocodingService.GeocodeAsync(
                        normalizedAddress, normalizedCity, country.CountryIsoCode, HttpContext.RequestAborted);

                var propertyEntity = new Property
                {
                    Name = normalizedName,
                    City = normalizedCity,
                    Address = normalizedAddress,
                    CountryIsoCode = country.CountryIsoCode,
                    CountryCode = country.CountryCode,
                    Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                    Latitude = request.Latitude ?? resolvedCoordinates?.Latitude,
                    Longitude = request.Longitude ?? resolvedCoordinates?.Longitude,
                    MapEnabled = false,
                    LandlordId = landlordId,
                    CreatedBy = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsDeleted = false
                };

                _context.Properties.Add(propertyEntity);
                await _context.SaveChangesAsync();

                var dto = new PropertyDetailDto
                {
                    Id = propertyEntity.Id,
                    Name = propertyEntity.Name,
                    City = propertyEntity.City,
                    Address = propertyEntity.Address,
                    CountryCode = propertyEntity.CountryCode,
                    CountryIsoCode = propertyEntity.CountryIsoCode,
                    Description = propertyEntity.Description,
                    Latitude = propertyEntity.Latitude,
                    Longitude = propertyEntity.Longitude,
                    MapEnabled = propertyEntity.MapEnabled,
                    SuccessDialogShowSuccessMessages = propertyEntity.SuccessDialogShowSuccessMessages,
                    SuccessDialogAutoCloseEnabled = propertyEntity.SuccessDialogAutoCloseEnabled,
                    SuccessDialogAutoCloseSeconds = propertyEntity.SuccessDialogAutoCloseSeconds,
                    SuccessDialogPosition = propertyEntity.SuccessDialogPosition,
                    LandlordId = propertyEntity.LandlordId,
                    LandlordName = landlord.FullName ?? landlord.Email ?? "",
                    Apartments = new List<ApartmentDto>()
                };

                return CreatedAtAction(nameof(GetProperty), new { id = propertyEntity.Id }, dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        private bool IsStripePayoutSetupRequired()
        {
            var connectEnabled = _configuration.GetValue<bool?>("Stripe:Connect:Enabled").GetValueOrDefault(false);
            return _configuration.GetValue<bool?>("Stripe:Connect:RequirePayoutSetup") ?? connectEnabled;
        }

        private static (string? CountryIsoCode, string? CountryCode) ResolvePropertyCountry(
            string? requestedCountryIsoCode,
            string? requestedCountryCode,
            string? currentCountryIsoCode,
            string? currentCountryCode,
            string? landlordCountryIsoCode,
            string? landlordCountryCode,
            bool requireCountry)
        {
            var countryIsoCode = NormalizeCountryIsoCode(requestedCountryIsoCode)
                ?? NormalizeCountryIsoCode(currentCountryIsoCode)
                ?? NormalizeCountryIsoCode(landlordCountryIsoCode)
                ?? ResolveCountryIsoFromCountryCode(requestedCountryCode)
                ?? ResolveCountryIsoFromCountryCode(currentCountryCode)
                ?? ResolveCountryIsoFromCountryCode(landlordCountryCode);

            var countryCode = NormalizeCountryCode(requestedCountryCode)
                ?? ResolveCountryCodeFromIso(countryIsoCode)
                ?? NormalizeCountryCode(currentCountryCode)
                ?? NormalizeCountryCode(landlordCountryCode);

            if (!requireCountry)
            {
                return (countryIsoCode, countryCode);
            }

            return string.IsNullOrWhiteSpace(countryIsoCode) || string.IsNullOrWhiteSpace(countryCode)
                ? (null, null)
                : (countryIsoCode, countryCode);
        }

        private static string? NormalizeCountryIsoCode(string? countryIsoCode)
        {
            var normalized = countryIsoCode?.Trim().ToUpperInvariant();
            return normalized is { Length: 2 } && normalized.All(char.IsLetter)
                ? normalized
                : null;
        }

        private static string? NormalizeCountryCode(string? countryCode)
        {
            var normalized = countryCode?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (!normalized.StartsWith("+", StringComparison.Ordinal))
            {
                normalized = $"+{normalized}";
            }

            return normalized.Length > 1 && normalized.Skip(1).All(char.IsDigit)
                ? normalized
                : null;
        }

        private static string? ResolveCountryCodeFromIso(string? countryIsoCode)
        {
            return NormalizeCountryIsoCode(countryIsoCode) switch
            {
                "CA" or "US" => "+1",
                "CM" => "+237",
                "GB" => "+44",
                "FR" => "+33",
                "BE" => "+32",
                "DE" => "+49",
                "NG" => "+234",
                "CI" => "+225",
                "GH" => "+233",
                "ZA" => "+27",
                "KE" => "+254",
                "AE" => "+971",
                _ => null
            };
        }

        private static string? ResolveCountryIsoFromCountryCode(string? countryCode)
        {
            return NormalizeCountryCode(countryCode) switch
            {
                "+237" => "CM",
                "+44" => "GB",
                "+33" => "FR",
                "+32" => "BE",
                "+49" => "DE",
                "+234" => "NG",
                "+225" => "CI",
                "+233" => "GH",
                "+27" => "ZA",
                "+254" => "KE",
                "+971" => "AE",
                "+1" => "CA",
                _ => null
            };
        }

        [HttpPut("{id}")]
        [Authorize]
        public async Task<IActionResult> UpdateProperty(int id, [FromBody] UpdatePropertyRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var user = await _userManager.FindByIdAsync(userId);
                if (user == null) return Unauthorized();

                var roles = (await _userManager.GetRolesAsync(user)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var isAdmin = roles.Contains("Admin");

                var property = await _context.Properties
                    .Include(p => p.Landlord)
                    .FirstOrDefaultAsync(p => p.Id == id);

                if (property == null) return NotFound("Property not found.");

                var canWrite = await _permissionService.HasPropertyPermissionAsync(userId, id, ManagerPermission.EditProperty, isAdmin);
                if (!canWrite) return Forbid();

                var name = request.Name.Trim();
                var city = request.City.Trim();
                var address = request.Address.Trim();

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(address))
                    return BadRequest("Name, city and address are required.");
                if (request.Latitude.HasValue != request.Longitude.HasValue)
                    return BadRequest("Latitude and longitude must be provided together.");

                var duplicate = await _context.Properties.AnyAsync(p =>
                    p.Id != id &&
                    p.LandlordId == property.LandlordId &&
                    p.Name.Trim().ToUpper() == name.ToUpper() &&
                    p.City.Trim().ToUpper() == city.ToUpper());

                if (duplicate)
                    return BadRequest($"A property named '{name}' already exists for this landlord in {city}.");

                property.Name = name;
                property.City = city;
                property.Address = address;
                var country = ResolvePropertyCountry(
                    request.CountryIsoCode,
                    request.CountryCode,
                    property.CountryIsoCode,
                    property.CountryCode,
                    property.Landlord?.CountryIsoCode,
                    property.Landlord?.CountryCode,
                    requireCountry: false);
                property.CountryIsoCode = country.CountryIsoCode;
                property.CountryCode = country.CountryCode;
                property.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
                var resolvedCoordinates = request.Latitude.HasValue
                    ? null
                    : await _geocodingService.GeocodeAsync(
                        address, city, country.CountryIsoCode, HttpContext.RequestAborted);
                property.Latitude = request.Latitude ?? resolvedCoordinates?.Latitude ?? property.Latitude;
                property.Longitude = request.Longitude ?? resolvedCoordinates?.Longitude ?? property.Longitude;
                property.UpdatedBy = userId;
                property.UpdatedAt = DateTimeOffset.UtcNow;

                _context.Properties.Update(property);
                await _context.SaveChangesAsync();

                var dto = new PropertyDetailDto
                {
                    Id = property.Id,
                    Name = property.Name,
                    City = property.City,
                    Address = property.Address,
                    CountryCode = property.CountryCode,
                    CountryIsoCode = property.CountryIsoCode,
                    Description = property.Description,
                    Latitude = property.Latitude,
                    Longitude = property.Longitude,
                    MapEnabled = property.MapEnabled,
                    SuccessDialogShowSuccessMessages = property.SuccessDialogShowSuccessMessages,
                    SuccessDialogAutoCloseEnabled = property.SuccessDialogAutoCloseEnabled,
                    SuccessDialogAutoCloseSeconds = property.SuccessDialogAutoCloseSeconds,
                    SuccessDialogPosition = property.SuccessDialogPosition,
                    LandlordId = property.LandlordId,
                    LandlordName = property.Landlord != null ? (property.Landlord.FullName ?? property.Landlord.Email ?? "") : "",
                    Apartments = new List<ApartmentDto>()
                };

                return Ok(dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPut("{id}/map-settings")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UpdatePropertyMapSettings(
            int id,
            [FromBody] UpdatePropertyMapSettingsRequest request)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            var property = await _context.Properties.FirstOrDefaultAsync(item => item.Id == id);
            if (property == null) return NotFound("Property not found.");

            property.MapEnabled = request.MapEnabled;
            property.UpdatedBy = userId;
            property.UpdatedAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new
            {
                property.Id,
                property.MapEnabled,
                Message = property.MapEnabled
                    ? "Property map enabled."
                    : "Property map disabled."
            });
        }

        [HttpPut("{id}/dialog-settings")]
        [Authorize]
        public async Task<IActionResult> UpdatePropertyDialogSettings(
            int id,
            [FromBody] UpdatePropertyDialogSettingsRequest request)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            var property = await _context.Properties.FirstOrDefaultAsync(item => item.Id == id && !item.IsDeleted);
            if (property == null) return NotFound("Property not found.");

            var isAdmin = User.IsInRole("Admin");
            if (!await _permissionService.HasPropertyPermissionAsync(
                    userId,
                    id,
                    ManagerPermission.EditProperty,
                    isAdmin))
            {
                return Forbid();
            }

            var normalizedPosition = request.DialogPosition?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalizedPosition) || !AllowedDialogPositions.Contains(normalizedPosition))
            {
                return BadRequest(new { Message = "The selected dialog position is invalid." });
            }

            property.SuccessDialogShowSuccessMessages = request.ShowSuccessMessages;
            property.SuccessDialogAutoCloseEnabled = request.AutoCloseEnabled;
            property.SuccessDialogAutoCloseSeconds = Math.Clamp(
                request.AutoCloseSeconds,
                MinDialogAutoCloseSeconds,
                MaxDialogAutoCloseSeconds);
            property.SuccessDialogPosition = normalizedPosition;
            property.UpdatedBy = userId;
            property.UpdatedAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new
            {
                property.Id,
                property.SuccessDialogShowSuccessMessages,
                property.SuccessDialogAutoCloseEnabled,
                property.SuccessDialogAutoCloseSeconds,
                property.SuccessDialogPosition,
                Message = "Property dialog settings updated."
            });
        }

        [HttpPost("{id}/geocode")]
        [Authorize]
        public async Task<IActionResult> RefreshPropertyCoordinates(int id)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
            var property = await _context.Properties.FirstOrDefaultAsync(item => item.Id == id);
            if (property == null) return NotFound("Property not found.");
            if (!property.MapEnabled)
            {
                return Conflict(new { Message = "The property map is disabled in Property Settings." });
            }
            if (!await _permissionService.HasPropertyPermissionAsync(
                    userId, id, ManagerPermission.EditProperty, User.IsInRole("Admin"))) return Forbid();

            var coordinates = await _geocodingService.GeocodeAsync(
                property.Address, property.City, property.CountryIsoCode, HttpContext.RequestAborted);
            if (coordinates == null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    Message = "The address could not be geocoded. Verify the address and the production Google Geocoding configuration."
                });
            }

            property.Latitude = coordinates.Latitude;
            property.Longitude = coordinates.Longitude;
            property.UpdatedBy = userId;
            property.UpdatedAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync();
            return Ok(new
            {
                property.Latitude,
                property.Longitude,
                coordinates.FormattedAddress,
                Message = "Property map location refreshed from its address."
            });
        }

        [HttpGet("{id}/overview")]
        [Authorize]
        public async Task<IActionResult> GetPropertyOverview(int id)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var user = await _userManager.FindByIdAsync(userId);
                if (user == null) return Unauthorized();

                var roles = (await _userManager.GetRolesAsync(user)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var isAdmin = roles.Contains("Admin");

                var property = await _context.Properties
                    .Include(p => p.Apartments)
                    .ThenInclude(a => a.Tenancies)
                    .ThenInclude(tenancy => tenancy.Members)
                    .Include(p => p.Landlord)
                    .FirstOrDefaultAsync(p => p.Id == id);

                if (property == null) return NotFound("Property not found.");

                var hasManagerAssignment = await _context.PropertyManagerAssignments.AnyAsync(assignment =>
                    !assignment.IsDeleted && assignment.PropertyId == id && assignment.ManagerId == userId);
                var hasApartmentOwnerAssignment = await _context.ApartmentOwners.AnyAsync(assignment =>
                    !assignment.IsDeleted &&
                    assignment.OwnerId == userId &&
                    assignment.Apartment != null &&
                    assignment.Apartment.PropertyId == id);
                var hasTenantAssignment = property.Apartments.Any(apartment =>
                    !apartment.IsDeleted &&
                    apartment.Tenancies.Any(tenancy =>
                        !tenancy.IsDeleted &&
                        tenancy.Members.Any(member => !member.IsDeleted && member.MemberId == userId)));
                var restrictToTenantAssignments = roles.Contains("Tenant") &&
                                                  !isAdmin &&
                                                  property.LandlordId != userId &&
                                                  !hasManagerAssignment &&
                                                  !hasApartmentOwnerAssignment;
                var isRestrictedManager = hasManagerAssignment && !isAdmin && property.LandlordId != userId;
                var managerCanViewOverview = isRestrictedManager &&
                    await _permissionService.HasPropertyPermissionAsync(
                        userId, id, ManagerPermission.ViewPropertyOverview, false);
                var hasAccess = isAdmin ||
                                property.LandlordId == userId ||
                                hasApartmentOwnerAssignment ||
                                hasTenantAssignment ||
                                managerCanViewOverview;
                if (!hasAccess)
                {
                    return Forbid();
                }

                var canWrite = await _permissionService.HasPropertyPermissionAsync(userId, id, ManagerPermission.EditProperty, isAdmin);
                var canManageManagers = await _permissionService.CanManageManagersAsync(userId, id, isAdmin);
                var canViewManagers = await _permissionService.HasPropertyPermissionAsync(userId, id, ManagerPermission.ViewManagers, isAdmin);
                var canViewDocuments = await _permissionService.HasPropertyPermissionAsync(userId, id, ManagerPermission.ViewDocuments, isAdmin);
                var canAddApartment = await _permissionService.HasPropertyPermissionAsync(userId, id, ManagerPermission.AddApartment, isAdmin);
                var canUploadDocuments = await _permissionService.HasPropertyPermissionAsync(userId, id, ManagerPermission.UploadDocuments, isAdmin);
                var canDeleteDocuments = await _permissionService.HasPropertyPermissionAsync(userId, id, ManagerPermission.DeleteDocuments, isAdmin);
                var managerApartmentIds = isRestrictedManager
                    ? await _permissionService.GetAccessibleApartmentIdsAsync(userId, id, ManagerPermission.ViewApartments, false)
                    : null;

                var propertyDto = new PropertyDetailDto
                {
                    Id = property.Id,
                    Name = property.Name,
                    City = property.City,
                    Address = property.Address,
                    CountryCode = property.CountryCode,
                    CountryIsoCode = property.CountryIsoCode,
                    Description = property.Description,
                    Latitude = property.Latitude,
                    Longitude = property.Longitude,
                    MapEnabled = property.MapEnabled,
                    SuccessDialogShowSuccessMessages = property.SuccessDialogShowSuccessMessages,
                    SuccessDialogAutoCloseEnabled = property.SuccessDialogAutoCloseEnabled,
                    SuccessDialogAutoCloseSeconds = property.SuccessDialogAutoCloseSeconds,
                    SuccessDialogPosition = property.SuccessDialogPosition,
                    LandlordId = property.LandlordId,
                    LandlordName = property.Landlord != null ? (property.Landlord.FullName ?? property.Landlord.Email ?? "") : "",
                    Apartments = property.Apartments
                        .Where(a =>
                            !a.IsDeleted &&
                            (managerApartmentIds == null || managerApartmentIds.Contains(a.Id)) &&
                            (!restrictToTenantAssignments || a.Tenancies.Any(tenancy =>
                                !tenancy.IsDeleted && tenancy.Members.Any(member => !member.IsDeleted && member.MemberId == userId))))
                        .OrderByDescending(a => a.CreatedAt)
                        .Select(a => new ApartmentDto
                        {
                            Id = a.Id,
                            Name = a.Name,
                            Type = a.Type.ToString(),
                            Price = a.Price,
                            Area = a.Area,
                            PropertyName = property.Name,
                            LandlordName = property.Landlord != null ? (property.Landlord.FullName ?? string.Empty) : string.Empty,
                            Status = ApartmentStatusResolver.Resolve(a.Tenancies, DateTimeOffset.UtcNow).ToString()
                        })
                        .ToList()
                };

                var managers = restrictToTenantAssignments || !canViewManagers
                    ? new List<PropertyManagerDto>()
                    : await _context.PropertyManagerAssignments
                        .Include(m => m.Manager)
                        .Where(m => m.PropertyId == id && !m.IsDeleted)
                        .OrderByDescending(m => m.CreatedAt)
                        .Select(m => new PropertyManagerDto
                        {
                            Id = m.Id,
                            ManagerId = m.ManagerId,
                            ManagerName = m.Manager != null ? (m.Manager.FullName ?? m.Manager.Email ?? "") : "",
                            Permission = m.Permission,
                            PermissionFlags = m.PermissionFlags,
                            AccessAllApartments = m.AccessAllApartments,
                            AssignedAt = m.CreatedAt
                        })
                        .ToListAsync();

                var documentEntities = canViewDocuments
                    ? await _context.Documents
                        .Where(d => d.PropertyId == id && d.ApartmentId == null && d.TenancyId == null)
                        .OrderByDescending(d => d.CreatedAt)
                        .ToListAsync()
                    : new List<Document>();

                var docs = await DocumentHelpers.ToDtosAsync(documentEntities, _storageService);

                var dto = new PropertyOverviewDto
                {
                    Property = propertyDto,
                    Managers = managers,
                    Documents = docs,
                    CanWrite = canWrite,
                    CanManageManagers = canManageManagers,
                    CanAddApartment = canAddApartment,
                    CanUploadDocuments = canUploadDocuments,
                    CanDeleteDocuments = canDeleteDocuments,
                    CanManageMapVisibility = isAdmin
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













