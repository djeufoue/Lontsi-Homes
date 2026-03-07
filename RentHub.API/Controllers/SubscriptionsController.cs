using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Models.Entities;
using System.Security.Claims;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SubscriptionsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public SubscriptionsController(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Approves a user subscription.  Only administrators can perform this action.  Once approved,
        /// the landlord will be allowed to add owners, managers and tenants.
        /// </summary>
        [HttpPost("approve/{subscriptionId}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ApproveSubscription(int subscriptionId)
        {
            try
            {
                var subscription = await _context.UserSubscriptions
                    .FirstOrDefaultAsync(us => us.Id == subscriptionId);
                if (subscription == null) return NotFound("Subscription not found.");

                subscription.IsApproved = true;
                // Audit: record approval details
                var adminId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);

                subscription.UpdatedBy = adminId;
                subscription.UpdatedAt = DateTime.UtcNow;
                _context.UserSubscriptions.Update(subscription);

                await _context.SaveChangesAsync();
                return Ok(new { Message = "Subscription approved." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Returns all available subscription plans.
        /// </summary>
        [HttpGet("plans")]
        public async Task<IActionResult> GetPlans()
        {
            try
            {
                var plans = await _context.SubscriptionPlans
                    .Select(p => new {
                        p.Id,
                        p.Name,
                        p.Price,
                        p.DurationInDays,
                        p.Description,
                        p.MaxProperties,
                        p.MaxApartmentsPerProperty
                    })
                    .ToListAsync();
                return Ok(plans);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Subscribes the current user to the specified plan.  The plan must exist.
        /// </summary>
        [HttpPost("subscribe/{planId}")]
        [Authorize]
        public async Task<IActionResult> Subscribe(int planId)
        {
            try
            {
                var plan = await _context.SubscriptionPlans
                    .FirstOrDefaultAsync(p => p.Id == planId);
                if (plan == null) return NotFound("Plan not found.");
                var userIdClaim = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userIdClaim)) return Unauthorized();
                // Cancel existing subscription(s) by updating end date and audit fields
                var existing = await _context.UserSubscriptions
                    .Where(us => us.UserId == userIdClaim && us.EndDate > DateTime.UtcNow)
                    .ToListAsync();
                foreach (var sub in existing)
                {
                    sub.EndDate = DateTime.UtcNow;
                    sub.UpdatedBy = userIdClaim;
                    sub.UpdatedAt = DateTime.UtcNow;
                    _context.UserSubscriptions.Update(sub);
                }
                var newSubscription = new UserSubscription
                {
                    UserId = userIdClaim,
                    SubscriptionPlanId = plan.Id,
                    StartDate = DateTime.UtcNow,
                    EndDate = DateTime.UtcNow.AddDays(plan.DurationInDays),
                    IsApproved = false,
                    // Audit: record creation
                    CreatedBy = userIdClaim,
                    CreatedAt = DateTime.UtcNow,
                    IsDeleted = false
                };
                _context.UserSubscriptions.Add(newSubscription);
                await _context.SaveChangesAsync();
                return Ok(new { Message = "Subscription activated", newSubscription.StartDate, newSubscription.EndDate });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpGet("pending")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetPendingSubscriptions()
        {
            try
            {
                var pending = await _context.UserSubscriptions
                    .Include(us => us.SubscriptionPlan)
                    .Where(us => !us.IsDeleted && !us.IsApproved && us.EndDate > DateTimeOffset.UtcNow)
                    .Select(us => new
                    {
                        us.Id,
                        us.UserId,
                        PlanName = us.SubscriptionPlan != null ? us.SubscriptionPlan.Name : "",
                        us.StartDate,
                        us.EndDate,
                        us.IsApproved
                    })
                    .OrderByDescending(x => x.StartDate)
                    .ToListAsync();

                return Ok(pending);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }
    }
}