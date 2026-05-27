using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Common.CommunicationModels;
using Common.Enums;
using Common.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
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
        private readonly ICamPayService _camPayService;
        private readonly ILogger<SubscriptionsController> _logger;

        public SubscriptionsController(
            ApplicationDbContext context,
            INotchPayService notchPayService,
            ICamPayService camPayService,
            ILogger<SubscriptionsController> logger)
        {
            _context = context;
            _notchPayService = notchPayService;
            _camPayService = camPayService;
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

                var normalizedPaymentMethod = SubscriptionPaymentMethodHelper.Normalize(request.PaymentMethod);
                if (normalizedPaymentMethod == PaymentMethodEnum.Card)
                {
                    return BadRequest(new
                    {
                        Code = "CARD_PAYMENTS_NOT_CONFIGURED",
                        Message = "Card payments are not configured yet. Please choose MTN Mobile Money or Orange Money."
                    });
                }

                var mobileMoneyPhoneNumber = ResolveMobileMoneyPhoneNumber(request.MobileMoneyPhoneNumber, user);
                if (!IsLikelyCameroonMobileMoneyNumber(mobileMoneyPhoneNumber))
                {
                    return BadRequest(new
                    {
                        Code = "MOBILE_MONEY_PHONE_REQUIRED",
                        Message = "Please provide a valid Cameroon Mobile Money phone number."
                    });
                }

                var validatedMobileMoneyPhoneNumber = mobileMoneyPhoneNumber!;
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

                var pendingSubscription = await LoadOpenSubscriptionAsync(userId, planId);

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
                    try
                    {
                        await _context.SaveChangesAsync();
                    }
                    catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
                    {
                        _context.Entry(pendingSubscription).State = EntityState.Detached;
                        pendingSubscription = await LoadOpenSubscriptionAsync(userId, planId);
                        if (pendingSubscription == null)
                        {
                            throw;
                        }
                    }
                }
                else if (pendingSubscription.PaymentStatus == PaymentStatusEnum.Pending &&
                         pendingSubscription.PaymentMethod == normalizedPaymentMethod &&
                         !string.IsNullOrWhiteSpace(pendingSubscription.PaymentProviderTransactionId))
                {
                    return Ok(BuildCheckoutSessionDto(
                        pendingSubscription,
                        plan,
                        normalizedPaymentMethod,
                        pendingSubscription.PaymentAuthorizationUrl ?? string.Empty,
                        "pending",
                        provider: "CamPay",
                        providerReference: pendingSubscription.PaymentProviderTransactionId,
                        paymentInstructions: "A Mobile Money payment request is already pending. Confirm it on your phone, then refresh your profile."));
                }
                else if (pendingSubscription.PaymentStatus == PaymentStatusEnum.Pending &&
                         pendingSubscription.PaymentAttemptCount > 0 &&
                         string.IsNullOrWhiteSpace(pendingSubscription.PaymentProviderTransactionId) &&
                         (pendingSubscription.UpdatedAt ?? pendingSubscription.CreatedAt) > now.AddMinutes(-2))
                {
                    return Conflict(new
                    {
                        Code = "SUBSCRIPTION_CHECKOUT_IN_PROGRESS",
                        Message = "A checkout is already being prepared. Please try again in a few seconds."
                    });
                }

                pendingSubscription.StartDate = now;
                pendingSubscription.EndDate = now.AddDays(plan.DurationInDays);
                pendingSubscription.PlanNameSnapshot = plan.Name;
                pendingSubscription.PlanPriceSnapshot = plan.Price;
                pendingSubscription.PlanDurationInDaysSnapshot = plan.DurationInDays;
                pendingSubscription.PlanMaxPropertiesSnapshot = plan.MaxProperties;
                pendingSubscription.PlanMaxApartmentsPerPropertySnapshot = plan.MaxApartmentsPerProperty;
                pendingSubscription.PaymentMethod = normalizedPaymentMethod;
                pendingSubscription.AllowAutomaticCardPayments = false;
                pendingSubscription.PaymentStatus = PaymentStatusEnum.Pending;
                pendingSubscription.PaymentCompletedAt = null;
                pendingSubscription.PaymentAttemptCount += 1;
                pendingSubscription.PaymentReference = BuildPaymentReference(
                    pendingSubscription.Id,
                    pendingSubscription.PaymentAttemptCount);
                pendingSubscription.PaymentAuthorizationUrl = null;
                pendingSubscription.PaymentProviderTransactionId = null;
                pendingSubscription.UpdatedBy = userId;
                pendingSubscription.UpdatedAt = now;
                _context.UserSubscriptions.Update(pendingSubscription);
                await _context.SaveChangesAsync();

                var checkout = await _camPayService.InitializeSubscriptionCheckoutAsync(
                    user,
                    plan,
                    pendingSubscription,
                    normalizedPaymentMethod,
                    validatedMobileMoneyPhoneNumber);

                pendingSubscription.PaymentAuthorizationUrl = null;
                pendingSubscription.PaymentProviderTransactionId = checkout.ProviderReference;
                _context.UserSubscriptions.Update(pendingSubscription);
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
                {
                    var existingSubscription = await _context.UserSubscriptions
                        .AsNoTracking()
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(us =>
                            !us.IsDeleted &&
                            (us.PaymentReference == pendingSubscription.PaymentReference ||
                             (!string.IsNullOrWhiteSpace(checkout.ProviderReference) &&
                              us.PaymentProviderTransactionId == checkout.ProviderReference)));

                    if (existingSubscription != null)
                    {
                        return Ok(BuildCheckoutSessionDto(
                            existingSubscription,
                            plan,
                            normalizedPaymentMethod,
                            existingSubscription.PaymentAuthorizationUrl ?? string.Empty,
                            "duplicate",
                            provider: "CamPay",
                            providerReference: existingSubscription.PaymentProviderTransactionId ?? string.Empty));
                    }

                    throw;
                }

                if (IsSuccessfulProviderStatus(checkout.Status))
                {
                    await ApplyPaymentStatusAsync(pendingSubscription, checkout.Status, checkout.ProviderReference, userId);
                }

                _logger.LogInformation(
                    "Subscription payment request created for {UserEmail} ({UserId}). SubscriptionId={SubscriptionId}, PlanId={PlanId}, PaymentMethod={PaymentMethod}, PaymentReference={PaymentReference}, Provider=CamPay, ProviderReference={ProviderReference}, ProviderStatus={ProviderStatus}",
                    user.Email ?? string.Empty,
                    user.Id,
                    pendingSubscription.Id,
                    plan.Id,
                    normalizedPaymentMethod,
                    pendingSubscription.PaymentReference,
                    checkout.ProviderReference,
                    checkout.Status);

                return Ok(BuildCheckoutSessionDto(
                    pendingSubscription,
                    plan,
                    normalizedPaymentMethod,
                    string.Empty,
                    checkout.Status,
                    provider: "CamPay",
                    providerReference: checkout.ProviderReference,
                    operatorName: checkout.Operator,
                    ussdCode: checkout.UssdCode,
                    paymentInstructions: BuildCamPayInstructions(checkout)));
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
                    if (IsCamPayPayment(subscription))
                    {
                        var remoteStatus = await _camPayService.RetrievePaymentAsync(
                            subscription.PaymentProviderTransactionId ?? reference);
                        if (remoteStatus != null &&
                            (string.IsNullOrWhiteSpace(remoteStatus.ExternalReference) ||
                             string.Equals(remoteStatus.ExternalReference, subscription.PaymentReference, StringComparison.Ordinal)))
                        {
                            await ApplyPaymentStatusAsync(subscription, remoteStatus.Status, remoteStatus.ProviderReference, userId);
                        }
                    }
                    else
                    {
                        var remoteStatus = await _notchPayService.RetrievePaymentAsync(reference);
                        if (remoteStatus != null)
                        {
                            await ApplyPaymentStatusAsync(subscription, remoteStatus.Status, remoteStatus.ProviderTransactionId, userId);
                        }
                    }
                }

                return Ok(new SubscriptionCheckoutStatusDto
                {
                    SubscriptionId = subscription.Id,
                    PlanId = subscription.SubscriptionPlanId,
                    PlanName = subscription.PlanNameSnapshot,
                    PaymentReference = subscription.PaymentReference,
                    ProviderReference = subscription.PaymentProviderTransactionId ?? string.Empty,
                    Provider = IsCamPayPayment(subscription) ? "CamPay" : "NotchPay",
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
                var eventId = ReadString(root, "id")
                    ?? ReadString(root, "event_id")
                    ?? ReadString(root, "eventId");
                var reference = ReadString(root, "reference") ?? ReadString(root, "payment_reference");
                var providerTransactionId = ReadString(root, "trxref") ?? ReadString(root, "transaction_id");
                var payloadHash = HashPayload(rawPayload);
                var eventKey = !string.IsNullOrWhiteSpace(eventId)
                    ? eventId.Trim()
                    : !string.IsNullOrWhiteSpace(reference)
                        ? HashKey($"{eventType}|{reference}|{providerTransactionId}")
                        : payloadHash;

                var webhookEvent = new PaymentWebhookEvent
                {
                    Provider = "NotchPay",
                    EventKey = eventKey,
                    EventType = eventType,
                    PaymentReference = reference,
                    ProviderTransactionId = providerTransactionId,
                    PayloadHash = payloadHash,
                    ReceivedAt = DateTimeOffset.UtcNow,
                    ProcessingStatus = "Received"
                };

                _context.PaymentWebhookEvents.Add(webhookEvent);
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
                {
                    return Ok(new { Message = "Webhook duplicate ignored." });
                }

                if (string.IsNullOrWhiteSpace(reference))
                {
                    webhookEvent.ProcessedAt = DateTimeOffset.UtcNow;
                    webhookEvent.ProcessingStatus = "Ignored";
                    webhookEvent.ProcessingMessage = "Missing payment reference.";
                    await _context.SaveChangesAsync();
                    return Ok(new { Message = "Webhook ignored." });
                }

                var subscription = await _context.UserSubscriptions
                    .FirstOrDefaultAsync(us => us.PaymentReference == reference && !us.IsDeleted);

                if (subscription == null)
                {
                    webhookEvent.ProcessedAt = DateTimeOffset.UtcNow;
                    webhookEvent.ProcessingStatus = "Ignored";
                    webhookEvent.ProcessingMessage = "No matching subscription.";
                    await _context.SaveChangesAsync();
                    return Ok(new { Message = "Webhook ignored." });
                }

                var processed = false;
                switch (eventType?.Trim().ToLowerInvariant())
                {
                    case "payment.complete":
                        await ApplyPaymentStatusAsync(subscription, "complete", providerTransactionId, "notchpay-webhook");
                        processed = true;
                        break;
                    case "payment.failed":
                    case "payment.canceled":
                    case "payment.cancelled":
                    case "payment.expired":
                        await ApplyPaymentStatusAsync(subscription, "failed", providerTransactionId, "notchpay-webhook");
                        processed = true;
                        break;
                }

                webhookEvent.ProcessedAt = DateTimeOffset.UtcNow;
                webhookEvent.ProcessingStatus = processed ? "Processed" : "Ignored";
                webhookEvent.ProcessingMessage = processed
                    ? "Subscription payment status updated."
                    : $"Unsupported event type: {eventType}";
                await _context.SaveChangesAsync();

                return Ok(new { Message = "Webhook received." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process Notch Pay webhook.");
                return Ok(new { Message = "Webhook received." });
            }
        }

        [AcceptVerbs("GET", "POST", Route = "campay/webhook")]
        [AllowAnonymous]
        public async Task<IActionResult> HandleCamPayWebhook()
        {
            var rawPayload = string.Empty;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in Request.Query)
            {
                values[item.Key] = item.Value.ToString();
            }

            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync();
                foreach (var item in form)
                {
                    values[item.Key] = item.Value.ToString();
                }

                rawPayload = SerializeValues(values);
            }
            else if (HttpMethods.IsPost(Request.Method))
            {
                using var reader = new StreamReader(Request.Body);
                rawPayload = await reader.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(rawPayload))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(rawPayload);
                        ReadJsonValues(document.RootElement, values);
                    }
                    catch (JsonException)
                    {
                        rawPayload = rawPayload.Trim();
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(rawPayload))
            {
                rawPayload = SerializeValues(values);
            }

            var providerReference = ReadValue(values, "reference");
            var externalReference = ReadValue(values, "external_reference");
            var providerStatus = ReadValue(values, "status");
            var operatorReference = ReadValue(values, "operator_reference");
            var eventKey = !string.IsNullOrWhiteSpace(providerReference)
                ? providerReference
                : !string.IsNullOrWhiteSpace(externalReference)
                    ? HashKey($"campay|{externalReference}|{providerStatus}")
                    : HashPayload(rawPayload);

            var webhookEvent = new PaymentWebhookEvent
            {
                Provider = "CamPay",
                EventKey = eventKey,
                EventType = providerStatus,
                PaymentReference = externalReference,
                ProviderTransactionId = providerReference,
                PayloadHash = HashPayload(rawPayload),
                ReceivedAt = DateTimeOffset.UtcNow,
                ProcessingStatus = "Received"
            };

            _context.PaymentWebhookEvents.Add(webhookEvent);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                return Ok(new { Message = "Webhook duplicate ignored." });
            }

            if (string.IsNullOrWhiteSpace(providerReference) && string.IsNullOrWhiteSpace(externalReference))
            {
                webhookEvent.ProcessedAt = DateTimeOffset.UtcNow;
                webhookEvent.ProcessingStatus = "Ignored";
                webhookEvent.ProcessingMessage = "Missing CamPay reference.";
                await _context.SaveChangesAsync();
                return Ok(new { Message = "Webhook ignored." });
            }

            var subscription = await _context.UserSubscriptions
                .FirstOrDefaultAsync(us =>
                    !us.IsDeleted &&
                    ((!string.IsNullOrWhiteSpace(externalReference) && us.PaymentReference == externalReference) ||
                     (!string.IsNullOrWhiteSpace(providerReference) && us.PaymentProviderTransactionId == providerReference)));

            if (subscription == null)
            {
                webhookEvent.ProcessedAt = DateTimeOffset.UtcNow;
                webhookEvent.ProcessingStatus = "Ignored";
                webhookEvent.ProcessingMessage = "No matching subscription.";
                await _context.SaveChangesAsync();
                return Ok(new { Message = "Webhook ignored." });
            }

            var remoteStatus = !string.IsNullOrWhiteSpace(providerReference)
                ? await _camPayService.RetrievePaymentAsync(providerReference)
                : null;

            if (remoteStatus != null &&
                !string.IsNullOrWhiteSpace(remoteStatus.ExternalReference) &&
                !string.Equals(remoteStatus.ExternalReference, subscription.PaymentReference, StringComparison.Ordinal))
            {
                webhookEvent.ProcessedAt = DateTimeOffset.UtcNow;
                webhookEvent.ProcessingStatus = "Ignored";
                webhookEvent.ProcessingMessage = "CamPay external reference does not match subscription.";
                await _context.SaveChangesAsync();
                return Ok(new { Message = "Webhook ignored." });
            }

            var verifiedStatus = remoteStatus?.Status ?? providerStatus;
            if (remoteStatus == null && !string.IsNullOrWhiteSpace(providerReference))
            {
                webhookEvent.ProcessedAt = DateTimeOffset.UtcNow;
                webhookEvent.ProcessingStatus = "Ignored";
                webhookEvent.ProcessingMessage = "Unable to verify CamPay transaction status.";
                await _context.SaveChangesAsync();
                return Ok(new { Message = "Webhook received." });
            }

            var processed = false;
            if (!string.IsNullOrWhiteSpace(verifiedStatus))
            {
                await ApplyPaymentStatusAsync(
                    subscription,
                    verifiedStatus,
                    remoteStatus?.ProviderReference ?? providerReference ?? operatorReference,
                    "campay-webhook");
                processed = IsTerminalProviderStatus(verifiedStatus);
            }

            webhookEvent.ProcessedAt = DateTimeOffset.UtcNow;
            webhookEvent.ProcessingStatus = processed ? "Processed" : "Ignored";
            webhookEvent.ProcessingMessage = processed
                ? "Subscription payment status updated."
                : $"CamPay status not terminal: {verifiedStatus}";
            await _context.SaveChangesAsync();

            return Ok(new { Message = "Webhook received." });
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
            if (subscription.PaymentStatus == PaymentStatusEnum.Success && subscription.IsApproved)
            {
                return;
            }

            var normalizedStatus = (providerStatus ?? string.Empty).Trim().ToLowerInvariant();
            if (IsSuccessfulProviderStatus(normalizedStatus))
            {
                subscription.PaymentProviderTransactionId = providerTransactionId ?? subscription.PaymentProviderTransactionId;
                await ActivateSubscriptionAsync(subscription, actor, markPaymentAsSuccess: true);
                var userEmail = await ResolveSubscriptionUserEmailAsync(subscription.UserId);
                _logger.LogInformation(
                    "Subscription payment succeeded for {UserEmail} ({UserId}). SubscriptionId={SubscriptionId}, PaymentReference={PaymentReference}, ProviderTransactionId={ProviderTransactionId}, ProviderStatus={ProviderStatus}, Actor={Actor}",
                    userEmail,
                    subscription.UserId,
                    subscription.Id,
                    subscription.PaymentReference,
                    subscription.PaymentProviderTransactionId,
                    providerStatus,
                    actor ?? "system");
                return;
            }

            if (IsFailedProviderStatus(normalizedStatus))
            {
                subscription.PaymentStatus = PaymentStatusEnum.Failed;
                subscription.IsApproved = false;
                subscription.PaymentProviderTransactionId = providerTransactionId ?? subscription.PaymentProviderTransactionId;
                subscription.UpdatedBy = actor;
                subscription.UpdatedAt = DateTimeOffset.UtcNow;
                _context.UserSubscriptions.Update(subscription);
                await _context.SaveChangesAsync();

                var userEmail = await ResolveSubscriptionUserEmailAsync(subscription.UserId);
                _logger.LogWarning(
                    "Subscription payment failed for {UserEmail} ({UserId}). SubscriptionId={SubscriptionId}, PaymentReference={PaymentReference}, ProviderTransactionId={ProviderTransactionId}, ProviderStatus={ProviderStatus}, Actor={Actor}",
                    userEmail,
                    subscription.UserId,
                    subscription.Id,
                    subscription.PaymentReference,
                    subscription.PaymentProviderTransactionId,
                    providerStatus,
                    actor ?? "system");
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

        private async Task<UserSubscription?> LoadOpenSubscriptionAsync(string userId, int planId)
        {
            return await _context.UserSubscriptions
                .Where(us =>
                    us.UserId == userId &&
                    us.SubscriptionPlanId == planId &&
                    !us.IsDeleted &&
                    !us.IsApproved &&
                    us.PaymentStatus != PaymentStatusEnum.Success)
                .OrderByDescending(us => us.CreatedAt)
                .FirstOrDefaultAsync();
        }

        private async Task<string> ResolveSubscriptionUserEmailAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return "unknown";
            }

            return await _context.Users
                .Where(user => user.Id == userId)
                .Select(user => user.Email ?? string.Empty)
                .FirstOrDefaultAsync() ?? "unknown";
        }

        private static SubscriptionCheckoutSessionDto BuildCheckoutSessionDto(
            UserSubscription subscription,
            SubscriptionPlan plan,
            PaymentMethodEnum paymentMethod,
            string authorizationUrl,
            string status,
            string provider = "",
            string providerReference = "",
            string operatorName = "",
            string ussdCode = "",
            string paymentInstructions = "")
        {
            return new SubscriptionCheckoutSessionDto
            {
                SubscriptionId = subscription.Id,
                PlanId = plan.Id,
                PlanName = !string.IsNullOrWhiteSpace(subscription.PlanNameSnapshot)
                    ? subscription.PlanNameSnapshot
                    : plan.Name,
                Amount = subscription.PlanPriceSnapshot > 0 ? subscription.PlanPriceSnapshot : plan.Price,
                Currency = "XAF",
                PaymentMethod = paymentMethod,
                AllowAutomaticCardPayments = subscription.AllowAutomaticCardPayments,
                PaymentReference = subscription.PaymentReference,
                AuthorizationUrl = authorizationUrl,
                Provider = provider,
                ProviderReference = providerReference,
                Operator = operatorName,
                UssdCode = ussdCode,
                PaymentInstructions = paymentInstructions,
                Status = status
            };
        }

        private static bool IsCamPayPayment(UserSubscription subscription)
        {
            return subscription.PaymentMethod is PaymentMethodEnum.Momo or PaymentMethodEnum.OrangeMoney;
        }

        private static bool IsSuccessfulProviderStatus(string providerStatus)
        {
            var normalizedStatus = (providerStatus ?? string.Empty).Trim().ToLowerInvariant();
            return normalizedStatus is "complete" or "success" or "successful";
        }

        private static bool IsFailedProviderStatus(string providerStatus)
        {
            var normalizedStatus = (providerStatus ?? string.Empty).Trim().ToLowerInvariant();
            return normalizedStatus is "failed" or "canceled" or "cancelled" or "expired";
        }

        private static bool IsTerminalProviderStatus(string providerStatus)
        {
            return IsSuccessfulProviderStatus(providerStatus) || IsFailedProviderStatus(providerStatus);
        }

        private static string BuildCamPayInstructions(CamPayCollectResult checkout)
        {
            var operatorName = string.IsNullOrWhiteSpace(checkout.Operator)
                ? "Mobile Money"
                : checkout.Operator;

            if (!string.IsNullOrWhiteSpace(checkout.UssdCode))
            {
                return $"Payment request sent through {operatorName}. Confirm it on your phone, or use {checkout.UssdCode} if prompted.";
            }

            return $"Payment request sent through {operatorName}. Confirm it on your phone to activate your subscription.";
        }

        private static string? ResolveMobileMoneyPhoneNumber(string? requestedPhoneNumber, ApplicationUser user)
        {
            if (!string.IsNullOrWhiteSpace(requestedPhoneNumber))
            {
                return requestedPhoneNumber;
            }

            if (!string.IsNullOrWhiteSpace(user.PhoneNumber))
            {
                return user.PhoneNumber;
            }

            return user.PayoutPhoneNumber;
        }

        private static bool IsLikelyCameroonMobileMoneyNumber(string? phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
            {
                return false;
            }

            var digits = new string(phoneNumber.Where(char.IsDigit).ToArray());
            if (digits.StartsWith("00", StringComparison.Ordinal))
            {
                digits = digits[2..];
            }

            return (digits.Length == 9 && digits.StartsWith("6", StringComparison.Ordinal)) ||
                   (digits.Length == 12 && digits.StartsWith("2376", StringComparison.Ordinal));
        }

        private static string BuildPaymentReference(int subscriptionId, int attemptCount)
        {
            return $"rhsub_{subscriptionId}_{attemptCount}";
        }

        private static string HashPayload(string value)
        {
            return HashKey(value);
        }

        private static string HashKey(string value)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static bool IsUniqueConstraintViolation(DbUpdateException ex)
        {
            if (ex.GetBaseException() is SqlException sqlException)
            {
                return sqlException.Number is 2601 or 2627;
            }

            var message = ex.GetBaseException().Message;
            return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
        }

        private static string? ReadValue(IReadOnlyDictionary<string, string> values, string propertyName)
        {
            return values.TryGetValue(propertyName, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
        }

        private static void ReadJsonValues(JsonElement element, IDictionary<string, string> values)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    {
                        values[property.Name] = property.Value.ToString();
                    }
                    else
                    {
                        ReadJsonValues(property.Value, values);
                    }
                }
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    ReadJsonValues(item, values);
                }
            }
        }

        private static string SerializeValues(IReadOnlyDictionary<string, string> values)
        {
            if (!values.Any())
            {
                return string.Empty;
            }

            return string.Join("&", values
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"{item.Key}={item.Value}"));
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
