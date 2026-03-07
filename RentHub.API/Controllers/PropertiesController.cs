using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Linq;
using RentHub.API.Data;
using RentHub.API.Models.Entities;
using Common.CommunicationModels;
using Common.Enums;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PropertiesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        public PropertiesController(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        /// <summary>
        /// Returns a list of all properties including the number of apartments.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetProperties()
        {
            try
            {
                var properties = await _context.Properties
                    .Include(p => p.Apartments)
                    .Select(p => new PropertyDto
                    {
                        Id = p.Id,
                        Name = p.Name,
                        City = p.City,
                        Address = p.Address,
                        ApartmentCount = p.Apartments.Count
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
        /// Returns details of a specific property.
        /// </summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetProperty(int id)
        {
            try
            {
                var property = await _context.Properties
                    .Include(p => p.Apartments)
                    .FirstOrDefaultAsync(p => p.Id == id);
                if (property == null) return NotFound();
                var dto = new PropertyDetailDto
                {
                    Id = property.Id,
                    Name = property.Name,
                    City = property.City,
                    Address = property.Address,
                    Apartments = property.Apartments.Select(a => new ApartmentDto
                    {
                        Id = a.Id,
                        Name = a.Name,
                        Type = a.Type.ToString(),
                        Price = a.Price,
                        Area = a.Area,
                        PropertyName = property.Name,
                        LandlordName = property.Landlord != null ? property.Landlord.FullName ?? string.Empty : string.Empty,
                        Status = a.Status.ToString()
                    }).ToList()
                };
                return Ok(dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Creates a new property.  The authenticated user becomes the landlord.
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> CreateProperty([FromBody] CreatePropertyRequest request)
        {
            try
            {
                // Extract the logged in user's identifier from the JWT/Identity cookie
                var userIdClaim = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userIdClaim))
                {
                    return Unauthorized();
                }

                // Normalize + validate name
                var normalizedName = (request?.Name ?? "").Trim();
                if (string.IsNullOrWhiteSpace(normalizedName))
                {
                    return BadRequest("Property name is required.");
                }

                // Check if another property already exists with the same name (case-insensitive)
                // Note: This assumes you want global uniqueness (not per landlord).
                // If your DB collation is case-insensitive, the Trim() == normalizedName is enough,
                // but ToUpper() makes it robust even on case-sensitive collations.
                var nameKey = normalizedName.ToUpper();

                var propertyExists = await _context.Properties.AnyAsync(p =>
                    !p.IsDeleted &&
                    p.Name != null &&
                    p.Name.Trim().ToUpper() == nameKey
                );

                if (propertyExists)
                {
                    return BadRequest($"A property with the name '{normalizedName}' already exists.");
                }

                // Check landlord's subscription plan for maximum properties
                var activeSubscription = await _context.UserSubscriptions
                    .Include(us => us.SubscriptionPlan)
                    .Where(us => us.UserId == userIdClaim && us.EndDate > DateTimeOffset.UtcNow)
                    .OrderByDescending(us => us.EndDate)
                    .FirstOrDefaultAsync();

                var maxProps = activeSubscription?.SubscriptionPlan?.MaxProperties;
                if (maxProps.HasValue)
                {
                    var currentCount = await _context.Properties.CountAsync(p => p.LandlordId == userIdClaim);
                    if (currentCount >= maxProps.Value)
                    {
                        return BadRequest($"Maximum number of properties ({maxProps.Value}) reached for your subscription plan.");
                    }
                }

                // Create property entity from request and assign landlord and audit fields
                var propertyEntity = new Property
                {
                    Name = normalizedName,
                    City = request.City,
                    Address = request.Address,
                    LandlordId = userIdClaim,
                    CreatedBy = userIdClaim,
                    CreatedAt = DateTime.UtcNow,
                    IsDeleted = false
                };

                _context.Properties.Add(propertyEntity);
                await _context.SaveChangesAsync();

                // Map to DTO for return
                var dto = new PropertyDetailDto
                {
                    Id = propertyEntity.Id,
                    Name = propertyEntity.Name,
                    City = propertyEntity.City,
                    Address = propertyEntity.Address,
                    Apartments = new List<ApartmentDto>()
                };

                return CreatedAtAction(nameof(GetProperty), new { id = propertyEntity.Id }, dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpGet("mine")]
        [Authorize]
        public async Task<IActionResult> GetMyProperties()
        {
            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var props = await _context.Properties
                    .Include(p => p.Apartments)
                    .Where(p => !p.IsDeleted && p.LandlordId == userId)
                    .Select(p => new PropertyDto
                    {
                        Id = p.Id,
                        Name = p.Name,
                        City = p.City,
                        Address = p.Address,
                        ApartmentCount = p.Apartments.Count(a => !a.IsDeleted)
                    })
                    .ToListAsync();

                return Ok(props);
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
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var property = await _context.Properties
                    .Include(p => p.Apartments)
                    .Include(p => p.Landlord)
                    .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted);

                if (property == null) return NotFound("Property not found.");

                // Access: landlord OR manager assigned to property
                var isLandlord = property.LandlordId == userId;

                var isManager = await _context.PropertyManagerAssignments.AnyAsync(m =>
                    m.PropertyId == id && m.ManagerId == userId && !m.IsDeleted);

                if (!isLandlord && !isManager) return Forbid();

                // CanWrite: landlord OR manager RW
                var canWrite = isLandlord || await _context.PropertyManagerAssignments.AnyAsync(m =>
                    m.PropertyId == id &&
                    m.ManagerId == userId &&
                    !m.IsDeleted &&
                    m.Permission == PermissionLevelEnum.ReadWrite);

                // property detail
                var propertyDto = new PropertyDetailDto
                {
                    Id = property.Id,
                    Name = property.Name,
                    City = property.City,
                    Address = property.Address,
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

                // managers
                var managers = await _context.PropertyManagerAssignments
                    .Include(m => m.Manager)
                    .Where(m => m.PropertyId == id && !m.IsDeleted)
                    .OrderByDescending(m => m.CreatedAt)
                    .Select(m => new PropertyManagerDto
                    {
                        Id = m.Id,
                        ManagerId = m.ManagerId,
                        ManagerName = m.Manager != null ? (m.Manager.FullName ?? m.Manager.Email ?? "") : "",
                        Permission = m.Permission
                    })
                    .ToListAsync();

                // documents
                var docs = await _context.Documents
                    .Where(d => d.PropertyId == id && d.ApartmentId == null && d.TenancyId == null && !d.IsDeleted)
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