using Common.CommunicationModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Models.Entities;
using System.Security.Claims;

using RentHub.API.Helpers;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SubscriptionsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<SubscriptionsController> _logger;

        public SubscriptionsController(ApplicationDbContext context, ILogger<SubscriptionsController> logger)
        {
            _context = context;
            _logger = logger;
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
                    .FirstOrDefaultAsync(us => us.Id == subscriptionId && !us.IsDeleted);
                if (subscription == null) return NotFound("Subscription not found.");

                var now = DateTimeOffset.UtcNow;
                var adminUserId = UserHelpers.GetUserId(User);

                subscription.IsApproved = true;
                subscription.UpdatedBy = adminUserId;
                subscription.UpdatedAt = now;

                // Keep only one active subscription per user after approval.
                var otherActiveSubscriptions = await _context.UserSubscriptions
                    .Where(us =>
                        us.UserId == subscription.UserId &&
                        us.Id != subscription.Id &&
                        !us.IsDeleted &&
                        us.EndDate > now)
                    .ToListAsync();

                foreach (var other in otherActiveSubscriptions)
                {
                    other.EndDate = now;
                    other.UpdatedBy = adminUserId;
                    other.UpdatedAt = now;
                }

                _context.UserSubscriptions.Update(subscription);
                await _context.SaveChangesAsync();

                return Ok(new
                {
                    Message = "Subscription approved.",
                    subscription.StartDate,
                    subscription.EndDate
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to approve subscription {SubscriptionId}", subscriptionId);
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    Code = "SUBSCRIPTION_APPROVAL_FAILED",
                    Message = "Unable to approve subscription right now. Please try again."
                });
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
                _logger.LogError(ex, "Failed to load subscription plans");
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    Code = "PLANS_FETCH_FAILED",
                    Message = "Unable to load subscription plans right now."
                });
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

                plan.UpdatedBy = UserHelpers.GetUserId(User);
                plan.UpdatedAt = DateTimeOffset.UtcNow;

                _context.SubscriptionPlans.Update(plan);
                await _context.SaveChangesAsync();

                return Ok(new { Message = "Subscription plan updated successfully." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update subscription plan {PlanId}", planId);
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    Code = "PLAN_UPDATE_FAILED",
                    Message = "Unable to update the subscription plan right now. Please try again."
                });
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
                var now = DateTimeOffset.UtcNow;

                var plan = await _context.SubscriptionPlans
                    .FirstOrDefaultAsync(p => p.Id == planId && !p.IsDeleted);
                if (plan == null) return NotFound("Plan not found.");

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();

                var activeSubscriptions = await _context.UserSubscriptions
                    .Where(us => us.UserId == userId && !us.IsDeleted && us.EndDate > now)
                    .OrderByDescending(us => us.EndDate)
                    .ThenByDescending(us => us.StartDate)
                    .ToListAsync();

                var currentSubscription = activeSubscriptions.FirstOrDefault();

                if (currentSubscription != null && currentSubscription.SubscriptionPlanId == plan.Id)
                {
                    return Ok(new
                    {
                        Message = "You are already on this subscription plan.",
                        currentSubscription.StartDate,
                        currentSubscription.EndDate,
                        currentSubscription.IsApproved
                    });
                }

                await using var tx = await _context.Database.BeginTransactionAsync();

                foreach (var sub in activeSubscriptions)
                {
                    sub.EndDate = now;
                    sub.UpdatedBy = userId;
                    sub.UpdatedAt = now;
                    _context.UserSubscriptions.Update(sub);
                }

                var newSubscription = new UserSubscription
                {
                    UserId = userId,
                    SubscriptionPlanId = plan.Id,
                    StartDate = now,
                    EndDate = now.AddDays(plan.DurationInDays),
                    PlanNameSnapshot = plan.Name,
                    PlanPriceSnapshot = plan.Price,
                    PlanDurationInDaysSnapshot = plan.DurationInDays,
                    PlanMaxPropertiesSnapshot = plan.MaxProperties,
                    PlanMaxApartmentsPerPropertySnapshot = plan.MaxApartmentsPerProperty,
                    // Preserve approved state when replacing an already approved active plan.
                    IsApproved = currentSubscription?.IsApproved == true,
                    CreatedBy = userId,
                    CreatedAt = now,
                    IsDeleted = false
                };

                _context.UserSubscriptions.Add(newSubscription);
                await _context.SaveChangesAsync();
                await tx.CommitAsync();

                return Ok(new
                {
                    Message = currentSubscription == null
                        ? "Subscription activated."
                        : "Subscription upgraded successfully.",
                    newSubscription.StartDate,
                    newSubscription.EndDate,
                    newSubscription.IsApproved
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to subscribe user to plan {PlanId}", planId);
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    Code = "SUBSCRIPTION_ACTIVATION_FAILED",
                    Message = "Unable to activate subscription right now. Please try again."
                });
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
                _logger.LogError(ex, "Failed to load pending subscriptions");
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    Code = "PENDING_SUBSCRIPTIONS_FETCH_FAILED",
                    Message = "Unable to load pending subscriptions right now."
                });
            }
        }
    }
}

