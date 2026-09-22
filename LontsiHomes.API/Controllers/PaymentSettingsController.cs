using Common.CommunicationModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LontsiHomes.API.Data;
using LontsiHomes.API.Helpers;
using LontsiHomes.API.Models.Entities;

namespace LontsiHomes.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class PaymentSettingsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public PaymentSettingsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] int? propertyId = null)
        {
            var platformEnabled = await PaymentAvailabilityHelper.IsPlatformAutomaticPaymentEnabledAsync(_context);
            bool? propertyEnabled = null;

            if (propertyId.HasValue)
            {
                propertyEnabled = await _context.Properties
                    .Where(property => property.Id == propertyId.Value)
                    .Select(property => (bool?)property.AutomaticPaymentsEnabled)
                    .FirstOrDefaultAsync();

                if (!propertyEnabled.HasValue)
                {
                    return NotFound("Property not found.");
                }
            }

            return Ok(BuildResponse(platformEnabled, propertyId, propertyEnabled));
        }

        [HttpPut("platform")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UpdatePlatform(UpdateAutomaticPaymentAvailabilityRequest request)
        {
            var actor = UserHelpers.GetUserId(User);
            var settings = await _context.PlatformPaymentSettings.FirstOrDefaultAsync(item => item.Id == 1);
            if (settings == null)
            {
                settings = new PlatformPaymentSettings { Id = 1 };
                _context.PlatformPaymentSettings.Add(settings);
            }

            settings.AutomaticPaymentsEnabled = request.Enabled;
            settings.UpdatedBy = actor;
            settings.UpdatedAt = DateTimeOffset.UtcNow;

            if (!request.Enabled)
            {
                await _context.Properties.ExecuteUpdateAsync(update => update
                    .SetProperty(property => property.AutomaticPaymentsEnabled, false)
                    .SetProperty(property => property.UpdatedBy, actor)
                    .SetProperty(property => property.UpdatedAt, DateTimeOffset.UtcNow));

                await _context.UserSubscriptions
                    .Where(subscription => subscription.AllowAutomaticCardPayments)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(subscription => subscription.AllowAutomaticCardPayments, false)
                        .SetProperty(subscription => subscription.UpdatedBy, actor)
                        .SetProperty(subscription => subscription.UpdatedAt, DateTimeOffset.UtcNow));
            }

            await _context.SaveChangesAsync();
            return Ok(BuildResponse(request.Enabled, null, null));
        }

        [HttpPut("properties/{propertyId:int}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UpdateProperty(
            int propertyId,
            UpdateAutomaticPaymentAvailabilityRequest request)
        {
            var platformEnabled = await PaymentAvailabilityHelper.IsPlatformAutomaticPaymentEnabledAsync(_context);
            if (request.Enabled && !platformEnabled)
            {
                return Conflict(new
                {
                    Code = "PLATFORM_AUTOMATIC_PAYMENTS_DISABLED",
                    Message = "Enable automatic payments for the platform before enabling them for a property."
                });
            }

            var property = await _context.Properties.FirstOrDefaultAsync(item => item.Id == propertyId);
            if (property == null)
            {
                return NotFound("Property not found.");
            }

            property.AutomaticPaymentsEnabled = request.Enabled;
            property.UpdatedBy = UserHelpers.GetUserId(User);
            property.UpdatedAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(BuildResponse(platformEnabled, propertyId, property.AutomaticPaymentsEnabled));
        }

        private static PaymentAvailabilityDto BuildResponse(
            bool platformEnabled,
            int? propertyId,
            bool? propertyEnabled)
        {
            var effective = platformEnabled && propertyEnabled.GetValueOrDefault(platformEnabled);
            return new PaymentAvailabilityDto
            {
                PlatformAutomaticPaymentsEnabled = platformEnabled,
                PropertyId = propertyId,
                PropertyAutomaticPaymentsEnabled = propertyEnabled,
                EffectiveAutomaticPaymentsEnabled = effective,
                Message = effective
                    ? "Automatic payments are enabled."
                    : PaymentAvailabilityHelper.AutomaticPaymentsUnavailableMessage
            };
        }
    }
}
