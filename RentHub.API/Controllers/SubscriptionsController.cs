using Common.CommunicationModels;
using Microsoft.AspNetCore.Authorization;
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
        /// Approves a user subscription. Only administrators can perform this action.
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
                subscription.UpdatedBy = User.FindFirstValue(ClaimTypes.NameIdentifier);
                subscription.UpdatedAt = DateTimeOffset.UtcNow;

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
                    .OrderBy(p => p.Price)
                    .Select(p => new SubscriptionPlanDto
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Price = p.Price,
                        DurationInDays = p.DurationInDays,
                        Description = p.Description ?? string.Empty,
                        MaxProperties = p.MaxProperties,
                        MaxApartmentsPerProperty = p.MaxApartmentsPerProperty
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
        /// Updates a subscription plan. This affects only future subscriptions;
        /// active subscriptions keep their plan snapshot values.
        /// </summary>
        [HttpPut("plans/{planId}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UpdatePlan(int planId, [FromBody] UpdateSubscriptionPlanRequest request)
        {
            try
            {
                if (!ModelState.IsValid) return BadRequest(ModelState);

                var plan = await _context.SubscriptionPlans.FirstOrDefaultAsync(p => p.Id == planId);
                if (plan == null) return NotFound("Plan not found.");

                if (!string.IsNullOrWhiteSpace(request.Name))
                    plan.Name = request.Name.Trim();

                if (request.Description != null)
                    plan.Description = request.Description.Trim();

                if (request.Price.HasValue)
                {
                    if (request.Price.Value < 0) return BadRequest("Price must be greater than or equal to 0.");
                    plan.Price = request.Price.Value;
                }

                if (request.DurationInDays.HasValue)
                {
                    if (request.DurationInDays.Value < 1) return BadRequest("DurationInDays must be at least 1.");
                    plan.DurationInDays = request.DurationInDays.Value;
                }

                if (request.MaxProperties.HasValue)
                {
                    plan.MaxProperties = request.MaxProperties.Value < 0 ? null : request.MaxProperties.Value;
                }

                if (request.MaxApartmentsPerProperty.HasValue)
                {
                    plan.MaxApartmentsPerProperty = request.MaxApartmentsPerProperty.Value < 0
                        ? null
                        : request.MaxApartmentsPerProperty.Value;
                }

                plan.UpdatedBy = User.FindFirstValue(ClaimTypes.NameIdentifier);
                plan.UpdatedAt = DateTimeOffset.UtcNow;

                _context.SubscriptionPlans.Update(plan);
                await _context.SaveChangesAsync();

                return Ok(new { Message = "Subscription plan updated successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Subscribes the current user to the specified plan.
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

                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var existing = await _context.UserSubscriptions
                    .Where(us => us.UserId == userId && us.EndDate > DateTimeOffset.UtcNow)
                    .ToListAsync();

                foreach (var sub in existing)
                {
                    sub.EndDate = DateTimeOffset.UtcNow;
                    sub.UpdatedBy = userId;
                    sub.UpdatedAt = DateTimeOffset.UtcNow;
                    _context.UserSubscriptions.Update(sub);
                }

                var newSubscription = new UserSubscription
                {
                    UserId = userId,
                    SubscriptionPlanId = plan.Id,
                    StartDate = DateTimeOffset.UtcNow,
                    EndDate = DateTimeOffset.UtcNow.AddDays(plan.DurationInDays),
                    PlanNameSnapshot = plan.Name,
                    PlanPriceSnapshot = plan.Price,
                    PlanDurationInDaysSnapshot = plan.DurationInDays,
                    PlanMaxPropertiesSnapshot = plan.MaxProperties,
                    PlanMaxApartmentsPerPropertySnapshot = plan.MaxApartmentsPerProperty,
                    IsApproved = false,
                    CreatedBy = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsDeleted = false
                };

                _context.UserSubscriptions.Add(newSubscription);
                await _context.SaveChangesAsync();

                return Ok(new
                {
                    Message = "Subscription activated",
                    newSubscription.StartDate,
                    newSubscription.EndDate
                });
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
                    .Include(us => us.User)
                    .Where(us => !us.IsDeleted && !us.IsApproved && us.EndDate > DateTimeOffset.UtcNow)
                    .OrderByDescending(us => us.StartDate)
                    .Select(us => new PendingSubscriptionDto
                    {
                        Id = us.Id,
                        UserId = us.UserId,
                        UserEmail = us.User != null ? (us.User.Email ?? string.Empty) : string.Empty,
                        UserFullName = us.User != null ? (us.User.FullName ?? string.Empty) : string.Empty,
                        SubscriptionPlanId = us.SubscriptionPlanId,
                        PlanName = !string.IsNullOrWhiteSpace(us.PlanNameSnapshot)
                            ? us.PlanNameSnapshot
                            : (us.SubscriptionPlan != null ? us.SubscriptionPlan.Name : string.Empty),
                        PlanPrice = us.PlanPriceSnapshot,
                        PlanDurationInDays = us.PlanDurationInDaysSnapshot,
                        StartDate = us.StartDate,
                        EndDate = us.EndDate,
                        IsApproved = us.IsApproved
                    })
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
