using System.Text.Json;
using Common.CommunicationModels;
using Common.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Helpers;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Payments;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SubscriptionsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly INotchPayService _notchPayService;
        private readonly ILogger<SubscriptionsController> _logger;

        public SubscriptionsController(
            ApplicationDbContext context,
            INotchPayService notchPayService,
            ILogger<SubscriptionsController> logger)
        {
            _context = context;
            _notchPayService = notchPayService;
            _logger = logger;
        }

        [HttpPost("approve/{subscriptionId}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ApproveSubscription(int subscriptionId)
        {
            try
            {
                var subscription = await _context.UserSubscriptions
                    .FirstOrDefaultAsync(us => us.Id == subscriptionId && !us.IsDeleted);
                if (subscription == null)
                {
                    return NotFound("Subscription not found.");
                }

                await ActivateSubscriptionAsync(subscription, UserHelpers.GetUserId(User), markPaymentAsSuccess: false);

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

        [HttpPut("plans/{planId}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UpdatePlan(int planId, [FromBody] UpdateSubscriptionPlanRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var plan = await _context.SubscriptionPlans.FirstOrDefaultAsync(p => p.Id == planId);
                if (plan == null)
                {
                    return NotFound("Plan not found.");
                }

                if (!string.IsNullOrWhiteSpace(request.Name))
                {
                    plan.Name = request.Name.Trim();
                }

                if (request.Description != null)
                {
                    plan.Description = request.Description.Trim();
                }

                if (request.Price.HasValue)
                {
                    if (request.Price.Value < 0)
                    {
                        return BadRequest("Price must be greater than or equal to 0.");
                    }

                    plan.Price = request.Price.Value;
                }

                if (request.DurationInDays.HasValue)
                {
                    if (request.DurationInDays.Value < 1)
                    {
                        return BadRequest("DurationInDays must be at least 1.");
                    }

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

        [HttpPost("subscribe/{planId}")]
        [Authorize]
        public IActionResult Subscribe(int planId)
        {
            return BadRequest(new
            {
                Code = "SUBSCRIPTION_CHECKOUT_REQUIRED",
                Message = "Use the subscription checkout flow to pay and activate this plan."
            });
        }

        [HttpPost("checkout/{planId}")]
        [Authorize]
        public async Task<IActionResult> StartCheckout(int planId, [FromBody] StartSubscriptionCheckoutRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(userId))
                {
                    return Unauthorized();
                }

                var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
                if (user == null)
                {
                    return Unauthorized();
                }

                var plan = await _context.SubscriptionPlans.FirstOrDefaultAsync(p => p.Id == planId && !p.IsDeleted);
                if (plan == null)
                {
                    return NotFound("Plan not found.");
                }

                var now = DateTimeOffset.UtcNow;
                var currentApproved = await _context.UserSubscriptions
                    .Where(us =>
                        us.UserId == userId &&
                        us.SubscriptionPlanId == planId &&
                        !us.IsDeleted &&
                        us.IsApproved &&
                        us.EndDate > now)
                    .OrderByDescending(us => us.EndDate)
                    .FirstOrDefaultAsync();

                if (currentApproved != null)
                {
                    return BadRequest("This subscription plan is already active on your account.");
                }

                var pendingSubscription = await _context.UserSubscriptions
                    .Where(us =>
                        us.UserId == userId &&
                        us.SubscriptionPlanId == planId &&
                        !us.IsDeleted &&
                        !us.IsApproved &&
                        us.PaymentStatus != PaymentStatusEnum.Success)
                    .OrderByDescending(us => us.CreatedAt)
                    .FirstOrDefaultAsync();

                if (pendingSubscription == null)
                {
                    pendingSubscription = new UserSubscription
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
                        PaymentStatus = PaymentStatusEnum.Pending,
                        CreatedBy = userId,
                        CreatedAt = now,
                        IsDeleted = false
                    };

                    _context.UserSubscriptions.Add(pendingSubscription);
                    await _context.SaveChangesAsync();
                }

                pendingSubscription.StartDate = now;
                pendingSubscription.EndDate = now.AddDays(plan.DurationInDays);
                pendingSubscription.PlanNameSnapshot = plan.Name;
                pendingSubscription.PlanPriceSnapshot = plan.Price;
                pendingSubscription.PlanDurationInDaysSnapshot = plan.DurationInDays;
                pendingSubscription.PlanMaxPropertiesSnapshot = plan.MaxProperties;
                pendingSubscription.PlanMaxApartmentsPerPropertySnapshot = plan.MaxApartmentsPerProperty;
                pendingSubscription.PaymentMethod = request.PaymentMethod;
                pendingSubscription.AllowAutomaticCardPayments =
                    request.PaymentMethod == PaymentMethodEnum.Card && request.AllowAutomaticCardPayments;
                pendingSubscription.PaymentStatus = PaymentStatusEnum.Pending;
                pendingSubscription.PaymentCompletedAt = null;
                pendingSubscription.PaymentReference = BuildPaymentReference(pendingSubscription.Id);
                pendingSubscription.UpdatedBy = userId;
                pendingSubscription.UpdatedAt = now;

                var checkout = await _notchPayService.InitializeSubscriptionCheckoutAsync(
                    user,
                    plan,
                    pendingSubscription,
                    request.PaymentMethod,
                    pendingSubscription.AllowAutomaticCardPayments);

                pendingSubscription.PaymentAuthorizationUrl = checkout.AuthorizationUrl;
                pendingSubscription.PaymentProviderTransactionId = checkout.ProviderPaymentId;
                _context.UserSubscriptions.Update(pendingSubscription);
                await _context.SaveChangesAsync();

                return Ok(new SubscriptionCheckoutSessionDto
                {
                    SubscriptionId = pendingSubscription.Id,
                    PlanId = plan.Id,
                    PlanName = plan.Name,
                    Amount = plan.Price,
                    Currency = "XAF",
                    PaymentMethod = request.PaymentMethod,
                    AllowAutomaticCardPayments = pendingSubscription.AllowAutomaticCardPayments,
                    PaymentReference = pendingSubscription.PaymentReference,
                    AuthorizationUrl = checkout.AuthorizationUrl,
                    Status = checkout.Status
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize subscription checkout for plan {PlanId}", planId);
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    Code = "SUBSCRIPTION_CHECKOUT_FAILED",
                    Message = "Unable to initialize the subscription payment right now. Please try again."
                });
            }
        }

        [HttpGet("checkout-status/{reference}")]
        [Authorize]
        public async Task<IActionResult> GetCheckoutStatus(string reference)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(userId))
                {
                    return Unauthorized();
                }

                var subscription = await _context.UserSubscriptions
                    .FirstOrDefaultAsync(us =>
                        us.PaymentReference == reference &&
                        us.UserId == userId &&
                        !us.IsDeleted);

                if (subscription == null)
                {
                    return NotFound("Subscription payment not found.");
                }

                if (subscription.PaymentStatus != PaymentStatusEnum.Success)
                {
                    var remoteStatus = await _notchPayService.RetrievePaymentAsync(reference);
                    if (remoteStatus != null)
                    {
                        await ApplyPaymentStatusAsync(subscription, remoteStatus.Status, remoteStatus.ProviderTransactionId, userId);
                    }
                }

                return Ok(new SubscriptionCheckoutStatusDto
                {
                    SubscriptionId = subscription.Id,
                    PlanId = subscription.SubscriptionPlanId,
                    PlanName = subscription.PlanNameSnapshot,
                    PaymentReference = subscription.PaymentReference,
                    PaymentStatus = subscription.PaymentStatus.ToString(),
                    SubscriptionApproved = subscription.IsApproved,
                    PaymentCompleted = subscription.PaymentStatus == PaymentStatusEnum.Success,
                    Message = subscription.PaymentStatus == PaymentStatusEnum.Success
                        ? "Subscription activated successfully."
                        : "Payment is still pending or needs another attempt."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve checkout status for reference {Reference}", reference);
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    Code = "SUBSCRIPTION_STATUS_FAILED",
                    Message = "Unable to verify the subscription payment right now. Please try again."
                });
            }
        }

        [HttpPost("notchpay/webhook")]
        [AllowAnonymous]
        public async Task<IActionResult> HandleNotchPayWebhook()
        {
            string rawPayload;
            using (var reader = new StreamReader(Request.Body))
            {
                rawPayload = await reader.ReadToEndAsync();
            }

            var signature = Request.Headers["X-Notch-Signature"].FirstOrDefault();
            if (!_notchPayService.VerifyWebhookSignature(rawPayload, signature))
            {
                return Unauthorized();
            }

            try
            {
                using var document = JsonDocument.Parse(rawPayload);
                var root = document.RootElement;
                var eventType = ReadString(root, "type");
                var reference = ReadString(root, "reference") ?? ReadString(root, "payment_reference");
                var providerTransactionId = ReadString(root, "trxref") ?? ReadString(root, "transaction_id");

                if (string.IsNullOrWhiteSpace(reference))
                {
                    return Ok(new { Message = "Webhook ignored." });
                }

                var subscription = await _context.UserSubscriptions
                    .FirstOrDefaultAsync(us => us.PaymentReference == reference && !us.IsDeleted);

                if (subscription == null)
                {
                    return Ok(new { Message = "Webhook ignored." });
                }

                switch (eventType?.Trim().ToLowerInvariant())
                {
                    case "payment.complete":
                        await ApplyPaymentStatusAsync(subscription, "complete", providerTransactionId, "notchpay-webhook");
                        break;
                    case "payment.failed":
                    case "payment.canceled":
                    case "payment.cancelled":
                    case "payment.expired":
                        await ApplyPaymentStatusAsync(subscription, "failed", providerTransactionId, "notchpay-webhook");
                        break;
                }

                return Ok(new { Message = "Webhook received." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process Notch Pay webhook.");
                return Ok(new { Message = "Webhook received." });
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

        private async Task ApplyPaymentStatusAsync(
            UserSubscription subscription,
            string providerStatus,
            string? providerTransactionId,
            string? actor)
        {
            var normalizedStatus = (providerStatus ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedStatus == "complete" || normalizedStatus == "success")
            {
                subscription.PaymentProviderTransactionId = providerTransactionId ?? subscription.PaymentProviderTransactionId;
                await ActivateSubscriptionAsync(subscription, actor, markPaymentAsSuccess: true);
                return;
            }

            if (normalizedStatus is "failed" or "canceled" or "cancelled" or "expired")
            {
                subscription.PaymentStatus = PaymentStatusEnum.Failed;
                subscription.IsApproved = false;
                subscription.PaymentProviderTransactionId = providerTransactionId ?? subscription.PaymentProviderTransactionId;
                subscription.UpdatedBy = actor;
                subscription.UpdatedAt = DateTimeOffset.UtcNow;
                _context.UserSubscriptions.Update(subscription);
                await _context.SaveChangesAsync();
            }
        }

        private async Task ActivateSubscriptionAsync(
            UserSubscription subscription,
            string? actor,
            bool markPaymentAsSuccess)
        {
            var now = DateTimeOffset.UtcNow;

            if (markPaymentAsSuccess)
            {
                subscription.PaymentStatus = PaymentStatusEnum.Success;
                subscription.PaymentCompletedAt = now;
            }

            subscription.IsApproved = true;
            subscription.UpdatedBy = actor;
            subscription.UpdatedAt = now;

            var otherActiveSubscriptions = await _context.UserSubscriptions
                .Where(us =>
                    us.UserId == subscription.UserId &&
                    us.Id != subscription.Id &&
                    !us.IsDeleted &&
                    us.IsApproved &&
                    us.EndDate > now)
                .ToListAsync();

            foreach (var other in otherActiveSubscriptions)
            {
                other.EndDate = now;
                other.UpdatedBy = actor;
                other.UpdatedAt = now;
            }

            _context.UserSubscriptions.Update(subscription);
            await _context.SaveChangesAsync();
        }

        private static string BuildPaymentReference(int subscriptionId)
        {
            return $"rhsub_{subscriptionId}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        }

        private static string? ReadString(JsonElement element, string propertyName)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        return property.Value.ValueKind switch
                        {
                            JsonValueKind.String => property.Value.GetString(),
                            JsonValueKind.Number => property.Value.ToString(),
                            _ => null
                        };
                    }

                    var nested = ReadString(property.Value, propertyName);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    var nested = ReadString(item, propertyName);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
            }

            return null;
        }
    }
}
