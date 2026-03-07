using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using Common.CommunicationModels;
using RentHub.API.Models.Entities;
using Common.Enums;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace RentHub.API.Controllers
{
    /// <summary>
    /// Provides endpoints to manage reminder settings for rent due and unpaid rent
    /// notifications.  Landlords can configure global or per-property settings.  Managers
    /// and owners with write permission can also update settings for properties they
    /// manage if allowed by the landlord.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class ReminderSettingsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public ReminderSettingsController(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Retrieves reminder settings for the current landlord.  This returns global settings
        /// (where PropertyId is null) and settings for each property owned by the landlord.
        /// </summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetReminderSettings()
        {
            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                // Determine if user is landlord; managers/owners cannot query settings globally
                var isLandlord = await _context.Properties.AnyAsync(p => p.LandlordId == userId);
                if (!isLandlord) return Forbid();
                var settings = await _context.ReminderSettings
                    .Where(rs => rs.LandlordId == userId)
                    .Select(rs => new ReminderSettingsDto
                    {
                        PropertyId = rs.PropertyId,
                        RentDueReminderDays = rs.RentDueReminderDays,
                        RentUnpaidReminderDays = rs.RentUnpaidReminderDays
                    })
                    .ToListAsync();
                return Ok(settings);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Creates or updates reminder settings for a specific property.  Only the landlord
        /// of the property or a manager/owner with write permission may modify the settings.
        /// </summary>
        [HttpPost("property/{propertyId}")]
        [Authorize]
        public async Task<IActionResult> UpsertPropertySettings(int propertyId, [FromBody] ReminderSettingsDto dto)
        {
            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                var property = await _context.Properties.FirstOrDefaultAsync(p => p.Id == propertyId);
                if (property == null) return NotFound("Property not found.");
                // Check permissions: landlord, manager write, owner write
                bool canWrite = false;
                if (property.LandlordId == userId)
                {
                    canWrite = true;
                }
                else
                {
                    var managerWrite = await _context.PropertyManagerAssignments
                        .AnyAsync(m => m.PropertyId == propertyId && m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite);
                    var ownerWrite = await _context.ApartmentOwners
                        .Include(o => o.Apartment)
                        .AnyAsync(o => o.Apartment!.PropertyId == propertyId && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite);
                    canWrite = managerWrite || ownerWrite;
                }
                if (!canWrite) return Forbid();
                // Validate dto
                if (dto.RentDueReminderDays < 0 || dto.RentUnpaidReminderDays < 0)
                    return BadRequest("Reminder days must be non-negative.");
                // Find existing settings
                var settings = await _context.ReminderSettings
                    .FirstOrDefaultAsync(rs => rs.LandlordId == property.LandlordId && rs.PropertyId == propertyId);
                if (settings == null)
                {
                    settings = new ReminderSettings
                    {
                        LandlordId = property.LandlordId,
                        PropertyId = propertyId,
                        RentDueReminderDays = dto.RentDueReminderDays,
                        RentUnpaidReminderDays = dto.RentUnpaidReminderDays,
                        CreatedBy = userId,
                        CreatedAt = DateTime.UtcNow,
                        IsDeleted = false
                    };
                    _context.ReminderSettings.Add(settings);
                }
                else
                {
                    settings.RentDueReminderDays = dto.RentDueReminderDays;
                    settings.RentUnpaidReminderDays = dto.RentUnpaidReminderDays;
                    settings.UpdatedBy = userId;
                    settings.UpdatedAt = DateTime.UtcNow;
                    _context.ReminderSettings.Update(settings);
                }
                await _context.SaveChangesAsync();
                return Ok(new { Message = "Reminder settings saved." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Creates or updates global reminder settings for the landlord.  These settings apply
        /// to all properties that do not have specific settings.  Only the landlord may
        /// modify their global settings.
        /// </summary>
        [HttpPost("landlord")]
        [Authorize]
        public async Task<IActionResult> UpsertLandlordSettings([FromBody] ReminderSettingsDto dto)
        {
            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                // Confirm user is a landlord (owns at least one property)
                var isLandlord = await _context.Properties.AnyAsync(p => p.LandlordId == userId);
                if (!isLandlord) return Forbid();
                if (dto.RentDueReminderDays < 0 || dto.RentUnpaidReminderDays < 0)
                    return BadRequest("Reminder days must be non-negative.");
                var settings = await _context.ReminderSettings
                    .FirstOrDefaultAsync(rs => rs.LandlordId == userId && rs.PropertyId == null);
                if (settings == null)
                {
                    settings = new ReminderSettings
                    {
                        LandlordId = userId,
                        PropertyId = null,
                        RentDueReminderDays = dto.RentDueReminderDays,
                        RentUnpaidReminderDays = dto.RentUnpaidReminderDays,
                        CreatedBy = userId,
                        CreatedAt = DateTime.UtcNow,
                        IsDeleted = false
                    };
                    _context.ReminderSettings.Add(settings);
                }
                else
                {
                    settings.RentDueReminderDays = dto.RentDueReminderDays;
                    settings.RentUnpaidReminderDays = dto.RentUnpaidReminderDays;
                    settings.UpdatedBy = userId;
                    settings.UpdatedAt = DateTime.UtcNow;
                    _context.ReminderSettings.Update(settings);
                }
                await _context.SaveChangesAsync();
                return Ok(new { Message = "Reminder settings saved." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }
    }
}