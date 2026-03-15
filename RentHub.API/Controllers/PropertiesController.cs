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

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PropertiesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IStorageService _storageService;

        public PropertiesController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IStorageService storageService)
        {
            _context = context;
            _userManager = userManager;
            _storageService = storageService;
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
                    .Where(m => m.ManagerId == userId)
                    .Select(m => new { m.PropertyId, m.Permission })
                    .ToListAsync();

                var managedAll = managed.Select(m => m.PropertyId).ToHashSet();
                var managedRw = managed
                    .Where(m => m.Permission == PermissionLevelEnum.ReadWrite)
                    .Select(m => m.PropertyId)
                    .ToHashSet();

                var ownerPropertyIds = await _context.ApartmentOwners
                    .Where(o => o.OwnerId == userId)
                    .Select(o => o.Apartment!.PropertyId)
                    .Distinct()
                    .ToListAsync();
                var ownerProps = ownerPropertyIds.ToHashSet();

                var tenantPropertyIds = await _context.Tenancies
                    .Where(t => t.Members.Any(mm => !mm.IsDeleted && mm.MemberId == userId))
                    .Select(t => t.Apartment!.PropertyId)
                    .Distinct()
                    .ToListAsync();
                var tenantProps = tenantPropertyIds.ToHashSet();

                var query = _context.Properties
                    .Include(p => p.Apartments)
                    .Include(p => p.Landlord)
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
                        p.Address.ToLower().Contains(s));
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
                    ApartmentCount = p.Apartments.Count(a => !a.IsDeleted),
                    LandlordId = p.LandlordId,
                    LandlordName = p.Landlord != null ? (p.Landlord.FullName ?? p.Landlord.Email ?? "") : "",
                    CanWrite = isAdmin || p.LandlordId == userId || managedRw.Contains(p.Id),
                    AccessSource = PropertyHelpers.ResolveAccessSource(isAdmin, p.LandlordId == userId, managedAll.Contains(p.Id), ownerProps.Contains(p.Id), tenantProps.Contains(p.Id))
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
                    var scope = await PropertyHelpers.BuildCreationScopeAsync(_context, userId, user.FullName ?? user.Email ?? "Landlord");
                    response.CreationScopes.Add(scope);
                    response.CanCreateProperty = response.CanCreateProperty || scope.CanCreate;
                }

                if (isManager)
                {
                    var managerLandlords = await _context.PropertyManagerAssignments
                        .Where(m => m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite)
                        .Join(_context.Properties.Include(p => p.Landlord), m => m.PropertyId, p => p.Id, (m, p) => new
                        {
                            p.LandlordId,
                            LandlordName = p.Landlord != null ? (p.Landlord.FullName ?? p.Landlord.Email ?? "") : "Landlord"
                        })
                        .Distinct()
                        .ToListAsync();

                    foreach (var landlord in managerLandlords)
                    {
                        var scope = await PropertyHelpers.BuildCreationScopeAsync(_context, landlord.LandlordId, landlord.LandlordName);
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
                        ApartmentCount = p.Apartments.Count(a => !a.IsDeleted),
                        LandlordId = p.LandlordId,
                        LandlordName = p.Landlord != null ? (p.Landlord.FullName ?? p.Landlord.Email ?? "") : "",
                        CanWrite = true,
                        AccessSource = "Owned"
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
                    .Include(p => p.Landlord)
                    .FirstOrDefaultAsync(p => p.Id == id);

                if (property == null) return NotFound("Property not found.");

                var hasAccess = await PropertyHelpers.CanAccessPropertyAsync(_context, id, userId, isAdmin);
                if (!hasAccess) return Forbid();

                var dto = new PropertyDetailDto
                {
                    Id = property.Id,
                    Name = property.Name,
                    City = property.City,
                    Address = property.Address,
                    Description = property.Description,
                    Latitude = property.Latitude,
                    Longitude = property.Longitude,
                    LandlordId = property.LandlordId,
                    LandlordName = property.Landlord != null ? (property.Landlord.FullName ?? property.Landlord.Email ?? "") : "",
                    Apartments = property.Apartments
                        .Where(a => !a.IsDeleted)
                        .Select(a => new ApartmentDto
                        {
                            Id = a.Id,
                            Name = a.Name,
                            Type = a.Type.ToString(),
                            Price = a.Price,
                            Area = a.Area,
                            PropertyName = property.Name,
                            LandlordName = property.Landlord != null ? (property.Landlord.FullName ?? string.Empty) : string.Empty,
                            Status = a.Status.ToString()
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
                        .Where(m => m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite)
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
                    var creationScope = await PropertyHelpers.BuildCreationScopeAsync(_context, landlordId, landlord.FullName ?? landlord.Email ?? "Landlord");
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

                var nameKey = normalizedName.ToUpperInvariant();
                var cityKey = normalizedCity.ToUpperInvariant();

                var propertyExists = await _context.Properties.AnyAsync(p =>
                    p.LandlordId == landlordId &&
                    p.Name.Trim().ToUpper() == nameKey &&
                    p.City.Trim().ToUpper() == cityKey);

                if (propertyExists)
                    return BadRequest($"A property named '{normalizedName}' already exists for this landlord in {normalizedCity}.");

                var propertyEntity = new Property
                {
                    Name = normalizedName,
                    City = normalizedCity,
                    Address = normalizedAddress,
                    Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                    Latitude = request.Latitude,
                    Longitude = request.Longitude,
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
                    Description = propertyEntity.Description,
                    Latitude = propertyEntity.Latitude,
                    Longitude = propertyEntity.Longitude,
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

                var canWrite = await PropertyHelpers.CanWritePropertyAsync(_context, id, userId, isAdmin);
                if (!canWrite) return Forbid();

                var name = request.Name.Trim();
                var city = request.City.Trim();
                var address = request.Address.Trim();

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(address))
                    return BadRequest("Name, city and address are required.");

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
                property.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
                property.Latitude = request.Latitude;
                property.Longitude = request.Longitude;
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
                    Description = property.Description,
                    Latitude = property.Latitude,
                    Longitude = property.Longitude,
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
                    .Include(p => p.Landlord)
                    .FirstOrDefaultAsync(p => p.Id == id);

                if (property == null) return NotFound("Property not found.");

                var hasAccess = await PropertyHelpers.CanAccessPropertyAsync(_context, id, userId, isAdmin);
                if (!hasAccess) return Forbid();

                var canWrite = await PropertyHelpers.CanWritePropertyAsync(_context, id, userId, isAdmin);

                var propertyDto = new PropertyDetailDto
                {
                    Id = property.Id,
                    Name = property.Name,
                    City = property.City,
                    Address = property.Address,
                    Description = property.Description,
                    Latitude = property.Latitude,
                    Longitude = property.Longitude,
                    LandlordId = property.LandlordId,
                    LandlordName = property.Landlord != null ? (property.Landlord.FullName ?? property.Landlord.Email ?? "") : "",
                    Apartments = property.Apartments
                        .Where(a => !a.IsDeleted)
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
                            Status = a.Status.ToString()
                        })
                        .ToList()
                };

                var managers = await _context.PropertyManagerAssignments
                    .Include(m => m.Manager)
                    .Where(m => m.PropertyId == id)
                    .OrderByDescending(m => m.CreatedAt)
                    .Select(m => new PropertyManagerDto
                    {
                        Id = m.Id,
                        ManagerId = m.ManagerId,
                        ManagerName = m.Manager != null ? (m.Manager.FullName ?? m.Manager.Email ?? "") : "",
                        Permission = m.Permission,
                        AssignedAt = m.CreatedAt
                    })
                    .ToListAsync();

                var documentEntities = await _context.Documents
                    .Where(d => d.PropertyId == id && d.ApartmentId == null && d.TenancyId == null)
                    .OrderByDescending(d => d.CreatedAt)
                    .ToListAsync();

                var docs = await DocumentHelpers.ToDtosAsync(documentEntities, _storageService);

                var dto = new PropertyOverviewDto
                {
                    Property = propertyDto,
                    Managers = managers,
                    Documents = docs,
                    CanWrite = canWrite
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













