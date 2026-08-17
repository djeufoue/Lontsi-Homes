using System.Globalization;
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
using RentHub.API.Services.Email;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SubscriptionsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly INotchPayService _notchPayService;
        private readonly ICamPayService _camPayService;
        private readonly IStripeCheckoutService _stripeCheckoutService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<SubscriptionsController> _logger;
        private readonly IEmailService _emailService;

        public SubscriptionsController(
            ApplicationDbContext context,
            INotchPayService notchPayService,
            ICamPayService camPayService,
            IStripeCheckoutService stripeCheckoutService,
            IEmailService emailService,
            IConfiguration configuration,
            ILogger<SubscriptionsController> logger)
        {
            _context = context;
            _notchPayService = notchPayService;
            _camPayService = camPayService;
            _stripeCheckoutService = stripeCheckoutService;
            _emailService = emailService;
            _configuration = configuration;
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

                if (subscription.PaymentMethod != PaymentMethodEnum.Cash &&
                    subscription.PaymentStatus != PaymentStatusEnum.Success)
                {
                    return Conflict(new
                    {
                        Code = "SUBSCRIPTION_PAYMENT_NOT_CONFIRMED",
                        Message = "The provider payment must be confirmed before this subscription can be approved."
                    });
                }

                await ActivateSubscriptionAsync(
                    subscription,
                    UserHelpers.GetUserId(User),
                    markPaymentAsSuccess: subscription.PaymentMethod == PaymentMethodEnum.Cash);

                var subscriber = await _context.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == subscription.UserId);
                if (!string.IsNullOrWhiteSpace(subscriber?.Email))
                {
                    try
                    {
                        var isFrench = subscriber.EmailLanguage == PlatformLanguage.French;
                        var culture = CultureInfo.GetCultureInfo(subscriber.EmailLanguage.ToCultureName());
                        await _emailService.SendEmailAsync(
                            subscriber.Email,
                            isFrench ? "Abonnement activé" : "Subscription activated",
                            isFrench
                                ? $"Votre abonnement {subscription.PlanNameSnapshot} a été approuvé et est maintenant actif jusqu’au {subscription.EndDate.ToString("d MMM yyyy", culture)}."
                                : $"Your {subscription.PlanNameSnapshot} subscription has been approved and is now active until {subscription.EndDate.ToString("dd MMM yyyy", culture)}.");
                    }
                    catch (Exception emailException)
                    {
                        _logger.LogWarning(emailException, "Subscription approval email failed for subscription {SubscriptionId}", subscription.Id);
                    }
                }

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
                    .OrderBy(p => p.DisplayOrder)
                    .ThenBy(p => p.Price)
                    .Select(p => new SubscriptionPlanDto
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Price = p.Price,
                        AnnualPrice = p.AnnualPrice,
                        DurationInDays = p.DurationInDays,
                        Description = p.Description ?? string.Empty,
                        MaxProperties = p.MaxProperties,
                        MaxApartmentsPerProperty = p.MaxApartmentsPerProperty,
                        MaxTotalApartments = p.MaxTotalApartments,
                        AudienceLabel = p.AudienceLabel ?? string.Empty,
                        FeatureHighlights = p.FeatureHighlights ?? string.Empty,
                        IsRecommended = p.IsRecommended,
                        IsContactSales = p.IsContactSales,
                        DisplayOrder = p.DisplayOrder
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

                if (request.AnnualPrice.HasValue)
                {
                    if (request.AnnualPrice.Value < 0)
                    {
                        return BadRequest("AnnualPrice must be greater than or equal to 0.");
                    }

                    plan.AnnualPrice = request.AnnualPrice.Value;
                }
                else if (request.IsContactSales == true)
                {
                    plan.AnnualPrice = null;
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

                if (request.MaxTotalApartments.HasValue)
                {
                    plan.MaxTotalApartments = request.MaxTotalApartments.Value < 0
                        ? null
                        : request.MaxTotalApartments.Value;
                }

                if (request.AudienceLabel != null)
                {
                    plan.AudienceLabel = request.AudienceLabel.Trim();
                }

                if (request.FeatureHighlights != null)
                {
                    plan.FeatureHighlights = request.FeatureHighlights.Trim();
                }

                if (request.IsRecommended.HasValue)
                {
                    plan.IsRecommended = request.IsRecommended.Value;
                }

                if (request.IsContactSales.HasValue)
                {
                    plan.IsContactSales = request.IsContactSales.Value;
                }

                if (request.DisplayOrder.HasValue)
                {
                    plan.DisplayOrder = Math.Max(0, request.DisplayOrder.Value);
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
                if (!await PaymentAvailabilityHelper.IsPlatformAutomaticPaymentEnabledAsync(_context))
                {
                    return StatusCode(StatusCodes.Status409Conflict, new
                    {
                        Code = "AUTOMATIC_PAYMENTS_DISABLED",
                        Message = "Automatic checkout is unavailable. Submit a manual subscription request instead."
                    });
                }

                request ??= new StartSubscriptionCheckoutRequest();

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

                if (User.IsInRole("Landlord"))
                {
                    var kycProfile = await _context.LandlordKycProfiles
                        .AsNoTracking()
                        .FirstOrDefaultAsync(p => p.UserId == userId);

                    if (kycProfile?.Status != LandlordKycStatusEnum.Approved)
                    {
                        return StatusCode(StatusCodes.Status403Forbidden, new
                        {
                            Code = "KYC_APPROVAL_REQUIRED",
                            Message = kycProfile == null
                                ? "Submit identity verification before paying for a subscription."
                                : kycProfile.Status == LandlordKycStatusEnum.Rejected
                                    ? "Identity verification was rejected. Submit corrected documents before subscribing."
                                    : "Identity verification is waiting for admin approval before payments are unlocked."
                        });
                    }

                    if (!user.PlatformTermsAccepted)
                    {
                        return StatusCode(StatusCodes.Status403Forbidden, new
                        {
                            Code = "PLATFORM_TERMS_REQUIRED",
                            Message = "Sign the platform contract before paying for a subscription."
                        });
                    }

                    if (IsStripePayoutSetupRequired() && !IsStripePayoutSetupComplete(user))
                    {
                        return StatusCode(StatusCodes.Status403Forbidden, new
                        {
                            Code = "STRIPE_PAYOUT_SETUP_REQUIRED",
                            Message = "Set up your payout account before paying for a subscription. This lets tenant rent payments be routed to you later."
                        });
                    }
                }

                var plan = await _context.SubscriptionPlans.FirstOrDefaultAsync(p => p.Id == planId && !p.IsDeleted);
                if (plan == null)
                {
                    return NotFound("Plan not found.");
                }

                if (plan.IsContactSales || plan.Price <= 0)
                {
                    return BadRequest(new
                    {
                        Code = "PLAN_REQUIRES_SALES_CONTACT",
                        Message = "This plan needs a custom quote. Please contact sales before checkout."
                    });
                }

                var requestedPaymentMethod = SubscriptionPaymentMethodHelper.Normalize(request.PaymentMethod);
                if (requestedPaymentMethod is PaymentMethodEnum.Momo or PaymentMethodEnum.OrangeMoney)
                {
                    return BadRequest(new
                    {
                        Code = "MOBILE_MONEY_TEMPORARILY_UNAVAILABLE",
                        Message = "MTN Mobile Money and Orange Money payments are not available yet. Please use card payment for this release."
                    });
                }

                var checkoutCurrency = ResolveStripeCurrency();
                var checkoutAmountMinorUnits = ConvertXafToStripeMinorUnits(plan.Price);
                var checkoutAmount = checkoutAmountMinorUnits / 100m;

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
                         pendingSubscription.PaymentMethod == requestedPaymentMethod &&
                         !string.IsNullOrWhiteSpace(pendingSubscription.PaymentProviderTransactionId) &&
                         IsStripeCheckoutClientSecret(pendingSubscription.PaymentAuthorizationUrl))
                {
                    var existingCheckoutUrl = BuildCardCheckoutUrl(pendingSubscription.PaymentReference);
                    return Ok(BuildCheckoutSessionDto(
                        pendingSubscription,
                        plan,
                        requestedPaymentMethod,
                        existingCheckoutUrl,
                        "pending",
                        provider: "Stripe",
                        providerReference: pendingSubscription.PaymentProviderTransactionId,
                        paymentInstructions: "Continue with the secure card form to complete this subscription.",
                        amount: checkoutAmount,
                        currency: checkoutCurrency,
                        clientSecret: pendingSubscription.PaymentAuthorizationUrl ?? string.Empty,
                        publishableKey: ResolveStripePublishableKey(),
                        returnUrl: BuildSubscriptionCallbackUrl(pendingSubscription.PaymentReference, pendingSubscription.PaymentProviderTransactionId)));
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
                pendingSubscription.PaymentMethod = requestedPaymentMethod;
                pendingSubscription.AllowAutomaticCardPayments = request.AllowAutomaticCardPayments;
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

                var checkout = await _stripeCheckoutService.CreateSubscriptionCheckoutAsync(
                    user,
                    plan,
                    pendingSubscription,
                    checkoutAmountMinorUnits,
                    checkoutCurrency);

                pendingSubscription.PaymentAuthorizationUrl = checkout.ClientSecret;
                pendingSubscription.PaymentProviderTransactionId = checkout.SessionId;
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
                             (!string.IsNullOrWhiteSpace(checkout.SessionId) &&
                              us.PaymentProviderTransactionId == checkout.SessionId)));

                    if (existingSubscription != null)
                    {
                        return Ok(BuildCheckoutSessionDto(
                            existingSubscription,
                            plan,
                            requestedPaymentMethod,
                            BuildCardCheckoutUrl(existingSubscription.PaymentReference),
                            "duplicate",
                            provider: "Stripe",
                            providerReference: existingSubscription.PaymentProviderTransactionId ?? string.Empty,
                            amount: checkoutAmount,
                            currency: checkoutCurrency,
                            clientSecret: existingSubscription.PaymentAuthorizationUrl ?? string.Empty,
                            publishableKey: ResolveStripePublishableKey(),
                            returnUrl: BuildSubscriptionCallbackUrl(
                                existingSubscription.PaymentReference,
                                existingSubscription.PaymentProviderTransactionId)));
                    }

                    throw;
                }

                var providerStatus = ResolveStripeProviderStatus(checkout.PaymentStatus, checkout.Status);
                if (IsSuccessfulProviderStatus(providerStatus))
                {
                    await ApplyPaymentStatusAsync(
                        pendingSubscription,
                        providerStatus,
                        checkout.SessionId,
                        userId,
                        checkout.CustomerId,
                        checkout.PaymentMethodId);
                }

                _logger.LogInformation(
                    "Subscription payment request created for {UserEmail} ({UserId}). SubscriptionId={SubscriptionId}, PlanId={PlanId}, PaymentMethod={PaymentMethod}, PaymentReference={PaymentReference}, Provider=Stripe, ProviderReference={ProviderReference}, ProviderStatus={ProviderStatus}",
                    user.Email ?? string.Empty,
                    user.Id,
                    pendingSubscription.Id,
                    plan.Id,
                    requestedPaymentMethod,
                    pendingSubscription.PaymentReference,
                    checkout.SessionId,
                    providerStatus);

                return Ok(BuildCheckoutSessionDto(
                    pendingSubscription,
                    plan,
                    requestedPaymentMethod,
                    BuildCardCheckoutUrl(pendingSubscription.PaymentReference),
                    providerStatus,
                    provider: "Stripe",
                    providerReference: checkout.SessionId,
                    paymentInstructions: "Enter your card details on the secure checkout page to complete the payment.",
                    amount: checkoutAmount,
                    currency: checkoutCurrency,
                    clientSecret: checkout.ClientSecret,
                    publishableKey: ResolveStripePublishableKey(),
                    returnUrl: checkout.ReturnUrl));
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
                    if (IsStripePayment(subscription))
                    {
                        var remoteStatus = await _stripeCheckoutService.RetrieveSessionAsync(
                            subscription.PaymentProviderTransactionId ?? string.Empty);
                        if (remoteStatus != null &&
                            (string.IsNullOrWhiteSpace(remoteStatus.PaymentReference) ||
                             string.Equals(remoteStatus.PaymentReference, subscription.PaymentReference, StringComparison.Ordinal)))
                        {
                            await ApplyPaymentStatusAsync(
                                subscription,
                                ResolveStripeProviderStatus(remoteStatus.PaymentStatus, remoteStatus.Status),
                                remoteStatus.SessionId,
                                userId,
                                remoteStatus.CustomerId,
                                remoteStatus.PaymentMethodId);
                        }
                    }
                    else if (IsCamPayPayment(subscription))
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

                var isStripe = IsStripePayment(subscription);
                var paymentCompleted = subscription.PaymentStatus == PaymentStatusEnum.Success;
                var providerName = isStripe
                    ? "Stripe"
                    : IsCamPayPayment(subscription) ? "CamPay" : "NotchPay";
                var statusMessage = paymentCompleted
                    ? "Subscription activated successfully."
                    : subscription.PaymentStatus is PaymentStatusEnum.Failed or PaymentStatusEnum.Error
                        ? "The card payment was not completed. Please check the card details or try another card."
                        : isStripe
                            ? "No card payment was completed. You can choose the plan and try again."
                            : "Payment is still pending or needs another attempt.";

                return Ok(new SubscriptionCheckoutStatusDto
                {
                    SubscriptionId = subscription.Id,
                    PlanId = subscription.SubscriptionPlanId,
                    PlanName = subscription.PlanNameSnapshot,
                    PaymentReference = subscription.PaymentReference,
                    ProviderReference = subscription.PaymentProviderTransactionId ?? string.Empty,
                    Provider = providerName,
                    PaymentStatus = subscription.PaymentStatus.ToString(),
                    SubscriptionApproved = subscription.IsApproved,
                    PaymentCompleted = paymentCompleted,
                    Message = statusMessage
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

        [HttpPost("manual-request/{planId:int}")]
        [Authorize(Roles = "Landlord")]
        public async Task<IActionResult> RequestManualActivation(
            int planId,
            [FromBody] ManualSubscriptionActivationRequest activationRequest)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            if (!ModelState.IsValid || activationRequest.DurationMonths is < 6 or > 36)
            {
                return BadRequest(new
                {
                    Message = "Choose a subscription duration between 6 and 36 months."
                });
            }

            var user = await _context.Users.FirstOrDefaultAsync(item => item.Id == userId);
            var plan = await _context.SubscriptionPlans.FirstOrDefaultAsync(item => item.Id == planId);
            if (user == null) return Unauthorized();
            if (plan == null) return NotFound("Plan not found.");
            if (plan.IsContactSales || plan.Price <= 0)
            {
                return BadRequest(new { Message = "This plan requires a custom sales agreement." });
            }

            if (User.IsInRole("Landlord"))
            {
                var kycProfile = await _context.LandlordKycProfiles
                    .AsNoTracking()
                    .FirstOrDefaultAsync(profile => profile.UserId == userId);

                if (kycProfile?.Status != LandlordKycStatusEnum.Approved)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        Code = "KYC_APPROVAL_REQUIRED",
                        Message = kycProfile == null
                            ? "Submit identity verification before requesting a subscription."
                            : kycProfile.Status == LandlordKycStatusEnum.Rejected
                                ? "Identity verification was rejected. Submit corrected documents before requesting a subscription."
                                : "Identity verification is waiting for admin approval before subscriptions are unlocked."
                    });
                }

                if (!user.PlatformTermsAccepted)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        Code = "PLATFORM_TERMS_REQUIRED",
                        Message = "Sign the platform contract before requesting a subscription."
                    });
                }
            }

            var automaticPaymentsEnabled = await PaymentAvailabilityHelper.IsPlatformAutomaticPaymentEnabledAsync(_context);
            if (automaticPaymentsEnabled)
            {
                return Conflict(new
                {
                    Code = "MANUAL_REQUEST_NOT_AVAILABLE",
                    Message = "Automatic payments are enabled. Use the normal checkout flow."
                });
            }

            var now = DateTimeOffset.UtcNow;
            var active = await _context.UserSubscriptions.AnyAsync(subscription =>
                subscription.UserId == userId && subscription.IsApproved &&
                subscription.PaymentStatus == PaymentStatusEnum.Success && subscription.EndDate > now);
            if (active)
            {
                return Conflict(new { Message = "An active subscription already exists for this account." });
            }

            var pendingRequests = await _context.UserSubscriptions
                .Where(subscription =>
                    subscription.UserId == userId &&
                    !subscription.IsDeleted &&
                    !subscription.IsApproved &&
                    subscription.PaymentStatus != PaymentStatusEnum.Success)
                .OrderByDescending(subscription => subscription.UpdatedAt ?? subscription.CreatedAt)
                .ToListAsync();
            var currentRequest = pendingRequests.FirstOrDefault();
            var requestedEndDate = now.AddMonths(activationRequest.DurationMonths);
            var requestedDurationInDays = Math.Max(1, (int)Math.Ceiling((requestedEndDate - now).TotalDays));
            var currentDurationMonths = currentRequest == null
                ? 0
                : Math.Clamp((int)Math.Round(currentRequest.PlanDurationInDaysSnapshot / 30.4375m), 1, 120);

            if (currentRequest?.SubscriptionPlanId == plan.Id && currentDurationMonths == activationRequest.DurationMonths)
            {
                return Conflict(new
                {
                    Code = "SUBSCRIPTION_REQUEST_ALREADY_PENDING",
                    Message = $"{plan.Name} for {activationRequest.DurationMonths} months is already waiting for administrator approval."
                });
            }

            var previousPlanName = currentRequest?.PlanNameSnapshot;
            var isPlanChange = currentRequest != null;
            if (pendingRequests.Count > 0)
            {
                foreach (var obsoleteRequest in pendingRequests)
                {
                    obsoleteRequest.IsDeleted = true;
                    obsoleteRequest.DeletedBy = userId;
                    obsoleteRequest.DeletedAt = now;
                    obsoleteRequest.UpdatedBy = userId;
                    obsoleteRequest.UpdatedAt = now;
                }

                // Preserve the previous request in the administrator history while
                // releasing the filtered unique index before creating its replacement.
                await _context.SaveChangesAsync();
            }

            var request = new UserSubscription
            {
                UserId = userId,
                SubscriptionPlanId = plan.Id,
                StartDate = now,
                EndDate = requestedEndDate,
                PlanNameSnapshot = plan.Name,
                PlanPriceSnapshot = CalculateSubscriptionPrice(plan, activationRequest.DurationMonths),
                PlanDurationInDaysSnapshot = requestedDurationInDays,
                PlanMaxPropertiesSnapshot = plan.MaxProperties,
                PlanMaxApartmentsPerPropertySnapshot = plan.MaxApartmentsPerProperty,
                PaymentMethod = PaymentMethodEnum.Cash,
                PaymentStatus = PaymentStatusEnum.Pending,
                AllowAutomaticCardPayments = false,
                IsApproved = false,
                PaymentAttemptCount = 1,
                PaymentReference = $"MANUAL-SUB-TEMP-{Guid.NewGuid():N}",
                CreatedBy = userId,
                CreatedAt = now,
                UpdatedBy = userId,
                UpdatedAt = now
            };
            _context.UserSubscriptions.Add(request);
            await _context.SaveChangesAsync();

            request.PaymentReference = $"MANUAL-SUB-{request.Id}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            await _context.SaveChangesAsync();

            var adminUsers = await _context.UserRoles
                .Join(_context.Roles.Where(role => role.Name == "Admin"), ur => ur.RoleId, role => role.Id, (ur, role) => ur.UserId)
                .Join(_context.Users, adminId => adminId, admin => admin.Id, (adminId, admin) => admin)
                .Where(admin => admin.Email != null && admin.Email != string.Empty)
                .Distinct()
                .ToListAsync();

            var adminUrl = $"{(_configuration["Portal:BaseUrl"] ?? "https://localhost:7059").TrimEnd('/')}/AdminSubscriptions";
            foreach (var admin in adminUsers)
            {
                try
                {
                    var isFrench = admin.EmailLanguage == PlatformLanguage.French;
                    var emailLines = new List<string>
                    {
                        isFrench
                            ? (isPlanChange ? "Un bailleur a modifié une demande d’abonnement manuel en attente." : "Une nouvelle demande d’activation manuelle d’abonnement attend votre examen.")
                            : (isPlanChange ? "A landlord changed a pending manual subscription request." : "A new manual subscription activation request is waiting for review."),
                        isFrench ? $"Bailleur : {user.FullName ?? user.Email}" : $"Landlord: {user.FullName ?? user.Email}",
                        isFrench ? $"Courriel : {user.Email}" : $"Email: {user.Email}"
                    };
                    if (isPlanChange && !string.IsNullOrWhiteSpace(previousPlanName))
                    {
                        emailLines.Add(isFrench ? $"Forfait précédent : {previousPlanName}" : $"Previous plan: {previousPlanName}");
                    }
                    emailLines.AddRange(new[]
                    {
                        isFrench ? $"Forfait sélectionné : {plan.Name}" : $"Selected plan: {plan.Name}",
                        isFrench ? $"Durée : {activationRequest.DurationMonths} mois" : $"Duration: {activationRequest.DurationMonths} months",
                        isFrench ? $"Montant : {request.PlanPriceSnapshot:N0} XAF" : $"Amount: {request.PlanPriceSnapshot:N0} XAF",
                        isFrench ? $"Référence : {request.PaymentReference}" : $"Reference: {request.PaymentReference}",
                        isFrench ? $"Examiner : {adminUrl}" : $"Review: {adminUrl}"
                    });
                    await _emailService.SendEmailAsync(
                        admin.Email!,
                        isFrench
                            ? (isPlanChange ? "Demande d’abonnement manuel modifiée" : "Nouvelle demande d’abonnement manuel")
                            : (isPlanChange ? "Manual subscription request changed" : "New manual subscription request"),
                        string.Join(Environment.NewLine, emailLines));
                }
                catch (Exception emailException)
                {
                    _logger.LogWarning(
                        emailException,
                        "Manual subscription request email failed for admin {AdminEmail} and subscription {SubscriptionId}",
                        admin.Email,
                        request.Id);
                }
            }

            return Ok(new
            {
                Message = isPlanChange
                    ? $"Your pending request was changed to {plan.Name} for {activationRequest.DurationMonths} months. An administrator was notified."
                    : "Your request was sent. An administrator will activate the subscription after confirming the cash payment.",
                SubscriptionId = request.Id,
                request.PaymentReference
            });
        }

        [HttpGet("checkout-session/{reference}")]
        [Authorize]
        public async Task<IActionResult> GetCheckoutSession(string reference)
        {
            try
            {
                if (!await PaymentAvailabilityHelper.IsPlatformAutomaticPaymentEnabledAsync(_context))
                {
                    return StatusCode(StatusCodes.Status409Conflict, new
                    {
                        Code = "AUTOMATIC_PAYMENTS_DISABLED",
                        Message = "Automatic card checkout is currently disabled."
                    });
                }

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(userId))
                {
                    return Unauthorized();
                }

                var subscription = await _context.UserSubscriptions
                    .Include(us => us.SubscriptionPlan)
                    .FirstOrDefaultAsync(us =>
                        us.PaymentReference == reference &&
                        us.UserId == userId &&
                        !us.IsDeleted);

                if (subscription == null || !IsStripePayment(subscription))
                {
                    return NotFound("Card checkout was not found.");
                }

                if (subscription.PaymentStatus == PaymentStatusEnum.Success)
                {
                    return BadRequest(new
                    {
                        Code = "SUBSCRIPTION_ALREADY_PAID",
                        Message = "This subscription payment has already been completed."
                    });
                }

                if (!IsStripeCheckoutClientSecret(subscription.PaymentAuthorizationUrl) ||
                    string.IsNullOrWhiteSpace(subscription.PaymentProviderTransactionId))
                {
                    return BadRequest(new
                    {
                        Code = "CARD_CHECKOUT_NOT_READY",
                        Message = "Card checkout is not ready yet. Please choose the plan again."
                    });
                }

                var plan = subscription.SubscriptionPlan;
                if (plan == null)
                {
                    return NotFound("Subscription plan was not found.");
                }

                var checkoutAmountMinorUnits = ConvertXafToStripeMinorUnits(
                    subscription.PlanPriceSnapshot > 0 ? subscription.PlanPriceSnapshot : plan.Price);
                var checkoutAmount = checkoutAmountMinorUnits / 100m;
                var checkoutCurrency = ResolveStripeCurrency();

                return Ok(BuildCheckoutSessionDto(
                    subscription,
                    plan,
                    PaymentMethodEnum.Card,
                    BuildCardCheckoutUrl(subscription.PaymentReference),
                    subscription.PaymentStatus.ToString(),
                    provider: "Stripe",
                    providerReference: subscription.PaymentProviderTransactionId,
                    paymentInstructions: "Enter your card details to complete the subscription payment.",
                    amount: checkoutAmount,
                    currency: checkoutCurrency,
                    clientSecret: subscription.PaymentAuthorizationUrl ?? string.Empty,
                    publishableKey: ResolveStripePublishableKey(),
                    returnUrl: BuildSubscriptionCallbackUrl(
                        subscription.PaymentReference,
                        subscription.PaymentProviderTransactionId)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load card checkout session for reference {Reference}", reference);
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    Code = "CARD_CHECKOUT_FETCH_FAILED",
                    Message = "Unable to load the card checkout right now. Please try again."
                });
            }
        }

        [HttpPost("stripe/webhook")]
        [AllowAnonymous]
        public async Task<IActionResult> HandleStripeWebhook()
        {
            string rawPayload;
            using (var reader = new StreamReader(Request.Body))
            {
                rawPayload = await reader.ReadToEndAsync();
            }

            var signature = Request.Headers["Stripe-Signature"].FirstOrDefault();
            if (!_stripeCheckoutService.VerifyWebhookSignature(rawPayload, signature))
            {
                return Unauthorized();
            }

            try
            {
                using var document = JsonDocument.Parse(rawPayload);
                var root = document.RootElement;
                var eventType = ReadDirectString(root, "type");
                var eventId = ReadDirectString(root, "id");
                var sessionElement = ResolveStripeEventObject(root);
                var session = StripeCheckoutService.ReadSession(sessionElement);
                var paymentReference = session?.PaymentReference ?? string.Empty;
                var providerTransactionId = session?.SessionId ?? string.Empty;
                var eventKey = !string.IsNullOrWhiteSpace(eventId)
                    ? eventId.Trim()
                    : !string.IsNullOrWhiteSpace(providerTransactionId)
                        ? providerTransactionId
                        : HashPayload(rawPayload);

                var webhookEvent = new PaymentWebhookEvent
                {
                    Provider = "Stripe",
                    EventKey = eventKey,
                    EventType = eventType,
                    PaymentReference = paymentReference,
                    ProviderTransactionId = providerTransactionId,
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

                if (string.IsNullOrWhiteSpace(eventType) ||
                    !eventType.StartsWith("checkout.session.", StringComparison.OrdinalIgnoreCase))
                {
                    webhookEvent.ProcessedAt = DateTimeOffset.UtcNow;
                    webhookEvent.ProcessingStatus = "Ignored";
                    webhookEvent.ProcessingMessage = $"Stripe event type is not handled by subscription checkout: {eventType}";
                    await _context.SaveChangesAsync();
                    return Ok(new { Message = "Webhook ignored." });
                }

                if (string.IsNullOrWhiteSpace(paymentReference) && string.IsNullOrWhiteSpace(providerTransactionId))
                {
                    webhookEvent.ProcessedAt = DateTimeOffset.UtcNow;
                    webhookEvent.ProcessingStatus = "Ignored";
                    webhookEvent.ProcessingMessage = "Missing Stripe checkout session reference.";
                    await _context.SaveChangesAsync();
                    return Ok(new { Message = "Webhook ignored." });
                }

                var subscription = await _context.UserSubscriptions
                    .FirstOrDefaultAsync(us =>
                        !us.IsDeleted &&
                        ((!string.IsNullOrWhiteSpace(paymentReference) && us.PaymentReference == paymentReference) ||
                         (!string.IsNullOrWhiteSpace(providerTransactionId) && us.PaymentProviderTransactionId == providerTransactionId)));

                if (subscription == null)
                {
                    webhookEvent.ProcessedAt = DateTimeOffset.UtcNow;
                    webhookEvent.ProcessingStatus = "Ignored";
                    webhookEvent.ProcessingMessage = "No matching subscription.";
                    await _context.SaveChangesAsync();
                    return Ok(new { Message = "Webhook ignored." });
                }

                if (!string.IsNullOrWhiteSpace(providerTransactionId))
                {
                    var enrichedSession = await _stripeCheckoutService.RetrieveSessionAsync(providerTransactionId);
                    if (enrichedSession != null &&
                        (string.IsNullOrWhiteSpace(enrichedSession.PaymentReference) ||
                         string.Equals(enrichedSession.PaymentReference, subscription.PaymentReference, StringComparison.Ordinal)))
                    {
                        session = enrichedSession;
                        paymentReference = string.IsNullOrWhiteSpace(enrichedSession.PaymentReference)
                            ? paymentReference
                            : enrichedSession.PaymentReference;
                        providerTransactionId = enrichedSession.SessionId;
                    }
                }

                var providerStatus = eventType?.Trim().ToLowerInvariant() switch
                {
                    "checkout.session.completed" => ResolveStripeProviderStatus(session?.PaymentStatus, session?.Status),
                    "checkout.session.expired" => "expired",
                    "checkout.session.async_payment_succeeded" => "paid",
                    "checkout.session.async_payment_failed" => "failed",
                    _ => ResolveStripeProviderStatus(session?.PaymentStatus, session?.Status)
                };

                var processed = false;
                if (IsTerminalProviderStatus(providerStatus))
                {
                    await ApplyPaymentStatusAsync(
                        subscription,
                        providerStatus,
                        providerTransactionId,
                        "stripe-webhook",
                        session?.CustomerId,
                        session?.PaymentMethodId);
                    processed = true;
                }

                webhookEvent.ProcessedAt = DateTimeOffset.UtcNow;
                webhookEvent.ProcessingStatus = processed ? "Processed" : "Ignored";
                webhookEvent.ProcessingMessage = processed
                    ? "Subscription payment status updated."
                    : $"Stripe status not terminal: {providerStatus}";
                await _context.SaveChangesAsync();

                return Ok(new { Message = "Webhook received." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process Stripe webhook.");
                return Ok(new { Message = "Webhook received." });
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
                    .Where(us =>
                        !us.IsDeleted &&
                        !us.IsApproved &&
                        us.EndDate > DateTimeOffset.UtcNow &&
                        us.PaymentMethod != PaymentMethodEnum.Card)
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
                        IsApproved = us.IsApproved,
                        PaymentMethod = us.PaymentMethod.HasValue ? us.PaymentMethod.Value.ToString() : "Manual",
                        PaymentReference = us.PaymentReference ?? string.Empty
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

        [HttpGet("admin")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetAdminSubscriptions([FromQuery] string? search = null)
        {
            var normalizedSearch = search?.Trim();
            var query = _context.UserSubscriptions
                .IgnoreQueryFilters()
                .Include(subscription => subscription.User)
                .Include(subscription => subscription.SubscriptionPlan)
                .AsNoTracking();

            if (!string.IsNullOrWhiteSpace(normalizedSearch))
            {
                query = query.Where(subscription =>
                    (subscription.User != null &&
                        ((subscription.User.FullName ?? string.Empty).Contains(normalizedSearch) ||
                         (subscription.User.Email ?? string.Empty).Contains(normalizedSearch))) ||
                    subscription.PlanNameSnapshot.Contains(normalizedSearch) ||
                    subscription.PaymentReference.Contains(normalizedSearch));
            }

            var subscriptions = await query
                .OrderBy(subscription =>
                    !subscription.IsDeleted &&
                    !subscription.IsApproved &&
                    subscription.PaymentStatus != PaymentStatusEnum.Success &&
                    subscription.PaymentStatus != PaymentStatusEnum.Failed
                        ? 0
                        : 1)
                .ThenByDescending(subscription => subscription.CreatedAt)
                .ThenByDescending(subscription => subscription.Id)
                .ToListAsync();

            return Ok(subscriptions.Select(ToAdminSubscriptionDto).ToList());
        }

        [HttpGet("admin/{subscriptionId:int}/history")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetAdminSubscriptionHistory(int subscriptionId)
        {
            var selected = await _context.UserSubscriptions
                .IgnoreQueryFilters()
                .Include(subscription => subscription.User)
                .Include(subscription => subscription.SubscriptionPlan)
                .AsNoTracking()
                .FirstOrDefaultAsync(subscription => subscription.Id == subscriptionId);

            if (selected == null)
            {
                return NotFound("Subscription not found.");
            }

            var history = await _context.UserSubscriptions
                .IgnoreQueryFilters()
                .Include(subscription => subscription.User)
                .Include(subscription => subscription.SubscriptionPlan)
                .AsNoTracking()
                .Where(subscription => subscription.UserId == selected.UserId)
                .OrderByDescending(subscription => subscription.CreatedAt)
                .ThenByDescending(subscription => subscription.Id)
                .ToListAsync();

            return Ok(new AdminSubscriptionHistoryDto
            {
                Subscription = ToAdminSubscriptionDto(selected),
                History = history.Select(ToAdminSubscriptionDto).ToList()
            });
        }

        [HttpPost("reject/{subscriptionId:int}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> RejectSubscription(int subscriptionId)
        {
            var subscription = await _context.UserSubscriptions
                .Include(item => item.User)
                .FirstOrDefaultAsync(item => item.Id == subscriptionId && !item.IsApproved && !item.IsDeleted);
            if (subscription == null) return NotFound("Pending subscription not found.");

            subscription.PaymentStatus = PaymentStatusEnum.Failed;
            subscription.IsDeleted = true;
            subscription.DeletedBy = UserHelpers.GetUserId(User);
            subscription.DeletedAt = DateTimeOffset.UtcNow;
            subscription.UpdatedBy = subscription.DeletedBy;
            subscription.UpdatedAt = subscription.DeletedAt;
            await _context.SaveChangesAsync();

            if (!string.IsNullOrWhiteSpace(subscription.User?.Email))
            {
                try
                {
                    var isFrench = subscription.User.EmailLanguage == PlatformLanguage.French;
                    await _emailService.SendEmailAsync(
                        subscription.User.Email,
                        isFrench ? "Mise à jour de votre demande d’abonnement" : "Subscription request update",
                        isFrench
                            ? $"Votre demande manuelle pour le forfait {subscription.PlanNameSnapshot} n’a pas été approuvée. Contactez l’administrateur de la plateforme pour plus d’informations."
                            : $"Your manual request for the {subscription.PlanNameSnapshot} plan was not approved. Contact the platform administrator if you need more information.");
                }
                catch (Exception emailException)
                {
                    _logger.LogWarning(emailException, "Subscription rejection email failed for subscription {SubscriptionId}", subscription.Id);
                }
            }

            return Ok(new { Message = "Subscription request rejected." });
        }

        [HttpGet("payment-activity")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetPaymentActivity(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25,
            [FromQuery] string? search = null,
            [FromQuery] string? status = null)
        {
            try
            {
                page = Math.Max(1, page);
                pageSize = Math.Clamp(pageSize, 10, 100);

                var query = _context.UserSubscriptions
                    .Include(subscription => subscription.User)
                    .Where(subscription =>
                        !subscription.IsDeleted &&
                        (!string.IsNullOrWhiteSpace(subscription.PaymentReference) ||
                         subscription.PaymentAttemptCount > 0 ||
                         subscription.PaymentCompletedAt != null));

                var normalizedSearch = (search ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(normalizedSearch))
                {
                    query = query.Where(subscription =>
                        subscription.PaymentReference.Contains(normalizedSearch) ||
                        (subscription.PaymentProviderTransactionId != null &&
                         subscription.PaymentProviderTransactionId.Contains(normalizedSearch)) ||
                        subscription.PlanNameSnapshot.Contains(normalizedSearch) ||
                        (subscription.User != null &&
                         ((subscription.User.Email != null && subscription.User.Email.Contains(normalizedSearch)) ||
                          (subscription.User.FullName != null && subscription.User.FullName.Contains(normalizedSearch)))));
                }

                var normalizedStatus = (status ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(normalizedStatus))
                {
                    if (string.Equals(normalizedStatus, "automatic", StringComparison.OrdinalIgnoreCase))
                    {
                        query = query.Where(subscription => subscription.IsAutomaticRenewal);
                    }
                    else if (Enum.TryParse<PaymentStatusEnum>(normalizedStatus, true, out var parsedStatus))
                    {
                        query = query.Where(subscription => subscription.PaymentStatus == parsedStatus);
                    }
                }

                var totalCount = await query.CountAsync();
                var summaryRows = await query
                    .Select(subscription => new
                    {
                        subscription.PaymentStatus,
                        subscription.PlanPriceSnapshot,
                        subscription.IsAutomaticRenewal
                    })
                    .ToListAsync();

                var pageRows = await query
                    .OrderByDescending(subscription => subscription.PaymentCompletedAt ?? subscription.UpdatedAt ?? subscription.CreatedAt)
                    .ThenByDescending(subscription => subscription.Id)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                var userIds = pageRows
                    .Select(subscription => subscription.UserId)
                    .Where(userId => !string.IsNullOrWhiteSpace(userId))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var propertyLookup = (await _context.Properties
                        .Where(property => !property.IsDeleted && userIds.Contains(property.LandlordId))
                        .OrderBy(property => property.Name)
                        .Select(property => new
                        {
                            property.LandlordId,
                            property.Id,
                            property.Name
                        })
                        .ToListAsync())
                    .GroupBy(property => property.LandlordId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

                var items = pageRows.Select(subscription =>
                {
                    propertyLookup.TryGetValue(subscription.UserId, out var property);

                    return new SubscriptionPaymentActivityDto
                    {
                        SubscriptionId = subscription.Id,
                        PlanId = subscription.SubscriptionPlanId,
                        PlanName = subscription.PlanNameSnapshot,
                        UserId = subscription.UserId,
                        UserEmail = subscription.User?.Email ?? string.Empty,
                        UserFullName = subscription.User?.FullName ?? string.Empty,
                        PropertyId = property?.Id,
                        PropertyName = property?.Name ?? "Account subscription",
                        PaymentReference = subscription.PaymentReference,
                        Provider = subscription.PaymentMethod == PaymentMethodEnum.Card
                            ? "Stripe"
                            : subscription.PaymentMethod is PaymentMethodEnum.Momo or PaymentMethodEnum.OrangeMoney
                                ? "Mobile Money"
                                : "Manual",
                        ProviderReference = subscription.PaymentProviderTransactionId ?? string.Empty,
                        PaymentMethod = subscription.PaymentMethod?.ToString() ?? "Unknown",
                        PaymentStatus = subscription.PaymentStatus.ToString(),
                        AmountUsd = ConvertXafToUsd(subscription.PlanPriceSnapshot),
                        AmountXaf = subscription.PlanPriceSnapshot,
                        Currency = ResolveStripeCurrency().ToUpperInvariant(),
                        AllowAutomaticCardPayments = subscription.AllowAutomaticCardPayments,
                        IsAutomaticRenewal = subscription.IsAutomaticRenewal,
                        CreatedAt = subscription.CreatedAt,
                        PaymentCompletedAt = subscription.PaymentCompletedAt,
                        StartDate = subscription.StartDate,
                        EndDate = subscription.EndDate,
                        ProcessingMessage = subscription.AutomaticPaymentFailureReason ?? string.Empty
                    };
                }).ToList();

                var successfulTotalXaf = summaryRows
                    .Where(row => row.PaymentStatus == PaymentStatusEnum.Success)
                    .Sum(row => row.PlanPriceSnapshot);
                var automaticPaymentsEnabled = await PaymentAvailabilityHelper.IsPlatformAutomaticPaymentEnabledAsync(_context);

                return Ok(new SubscriptionPaymentActivityResponseDto
                {
                    Items = items,
                    Page = page,
                    PageSize = pageSize,
                    TotalCount = totalCount,
                    TotalPages = totalCount == 0 ? 1 : (int)Math.Ceiling(totalCount / (decimal)pageSize),
                    Search = normalizedSearch,
                    Status = normalizedStatus,
                    AutomaticPaymentsEnabled = automaticPaymentsEnabled,
                    Summary = new SubscriptionPaymentActivitySummaryDto
                    {
                        TotalTransactions = summaryRows.Count,
                        SuccessfulTransactions = summaryRows.Count(row => row.PaymentStatus == PaymentStatusEnum.Success),
                        FailedTransactions = summaryRows.Count(row => row.PaymentStatus is PaymentStatusEnum.Failed or PaymentStatusEnum.Error),
                        PendingTransactions = summaryRows.Count(row => row.PaymentStatus == PaymentStatusEnum.Pending),
                        AutomaticRenewals = summaryRows.Count(row => row.IsAutomaticRenewal),
                        TotalSuccessfulXaf = successfulTotalXaf,
                        TotalSuccessfulUsd = ConvertXafToUsd(successfulTotalXaf)
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load subscription payment activity");
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    Code = "PAYMENT_ACTIVITY_FETCH_FAILED",
                    Message = "Unable to load payment activity right now."
                });
            }
        }

        private async Task ApplyPaymentStatusAsync(
            UserSubscription subscription,
            string providerStatus,
            string? providerTransactionId,
            string? actor,
            string? stripeCustomerId = null,
            string? stripePaymentMethodId = null)
        {
            if (subscription.PaymentStatus == PaymentStatusEnum.Success && subscription.IsApproved)
            {
                return;
            }

            var normalizedStatus = (providerStatus ?? string.Empty).Trim().ToLowerInvariant();
            if (IsSuccessfulProviderStatus(normalizedStatus))
            {
                subscription.PaymentProviderTransactionId = providerTransactionId ?? subscription.PaymentProviderTransactionId;
                if (subscription.AllowAutomaticCardPayments)
                {
                    if (!string.IsNullOrWhiteSpace(stripeCustomerId))
                    {
                        subscription.StripeCustomerId = stripeCustomerId;
                    }

                    if (!string.IsNullOrWhiteSpace(stripePaymentMethodId))
                    {
                        subscription.StripePaymentMethodId = stripePaymentMethodId;
                    }
                }

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

            if (!subscription.IsApproved)
            {
                subscription.StartDate = now;
                subscription.EndDate = now.AddDays(Math.Max(1, subscription.PlanDurationInDaysSnapshot));
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
            string paymentInstructions = "",
            decimal? amount = null,
            string currency = "XAF",
            string clientSecret = "",
            string publishableKey = "",
            string returnUrl = "")
        {
            return new SubscriptionCheckoutSessionDto
            {
                SubscriptionId = subscription.Id,
                PlanId = plan.Id,
                PlanName = !string.IsNullOrWhiteSpace(subscription.PlanNameSnapshot)
                    ? subscription.PlanNameSnapshot
                    : plan.Name,
                Amount = amount ?? (subscription.PlanPriceSnapshot > 0 ? subscription.PlanPriceSnapshot : plan.Price),
                Currency = string.IsNullOrWhiteSpace(currency) ? "XAF" : currency,
                PaymentMethod = paymentMethod,
                AllowAutomaticCardPayments = subscription.AllowAutomaticCardPayments,
                PaymentReference = subscription.PaymentReference,
                AuthorizationUrl = authorizationUrl,
                ClientSecret = clientSecret,
                PublishableKey = publishableKey,
                ReturnUrl = returnUrl,
                Provider = provider,
                ProviderReference = providerReference,
                Operator = operatorName,
                UssdCode = ussdCode,
                PaymentInstructions = paymentInstructions,
                Status = status
            };
        }

        private static bool IsStripePayment(UserSubscription subscription)
        {
            return subscription.PaymentMethod == PaymentMethodEnum.Card;
        }

        private static bool IsStripeCheckoutClientSecret(string? value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return normalized.StartsWith("cs_", StringComparison.OrdinalIgnoreCase) &&
                   normalized.Contains("_secret_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCamPayPayment(UserSubscription subscription)
        {
            return subscription.PaymentMethod is PaymentMethodEnum.Momo or PaymentMethodEnum.OrangeMoney;
        }

        private static bool IsSuccessfulProviderStatus(string providerStatus)
        {
            var normalizedStatus = (providerStatus ?? string.Empty).Trim().ToLowerInvariant();
            return normalizedStatus is "complete" or "success" or "successful" or "succeeded" or "paid";
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

        private static string ResolveStripeProviderStatus(string? paymentStatus, string? sessionStatus)
        {
            var normalizedPaymentStatus = (paymentStatus ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(normalizedPaymentStatus) &&
                normalizedPaymentStatus != "unpaid")
            {
                return normalizedPaymentStatus;
            }

            return (sessionStatus ?? normalizedPaymentStatus ?? "open").Trim().ToLowerInvariant();
        }

        private string BuildCardCheckoutUrl(string paymentReference)
        {
            var portalBaseUrl = _configuration["Portal:BaseUrl"]?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(portalBaseUrl))
            {
                throw new InvalidOperationException("Portal base URL is missing.");
            }

            return $"{portalBaseUrl}/Profile/CardCheckout?reference={Uri.EscapeDataString(paymentReference)}";
        }

        private string BuildSubscriptionCallbackUrl(string paymentReference, string? providerReference)
        {
            var portalBaseUrl = _configuration["Portal:BaseUrl"]?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(portalBaseUrl))
            {
                throw new InvalidOperationException("Portal base URL is missing.");
            }

            var url = $"{portalBaseUrl}/Profile/SubscriptionCallback?reference={Uri.EscapeDataString(paymentReference)}";
            return string.IsNullOrWhiteSpace(providerReference)
                ? url
                : $"{url}&session_id={Uri.EscapeDataString(providerReference)}";
        }

        private string ResolveStripePublishableKey()
        {
            return _configuration["Stripe:PublishableKey"]?.Trim() ?? string.Empty;
        }

        private string ResolveStripeCurrency()
        {
            var configured = (_configuration["Stripe:Currency"] ?? "usd").Trim();
            return string.IsNullOrWhiteSpace(configured)
                ? "USD"
                : configured.ToUpperInvariant();
        }

        private static decimal CalculateSubscriptionPrice(SubscriptionPlan plan, int durationMonths)
        {
            var fullYears = durationMonths / 12;
            var remainingMonths = durationMonths % 12;
            var annualAmount = plan.AnnualPrice ?? plan.Price * 12m;
            return fullYears * annualAmount + remainingMonths * plan.Price;
        }

        private static PendingSubscriptionDto ToAdminSubscriptionDto(UserSubscription subscription)
        {
            var status = subscription.IsDeleted
                ? subscription.PaymentStatus == PaymentStatusEnum.Failed
                    ? "Rejected"
                    : "Replaced"
                : subscription.IsApproved && subscription.EndDate > DateTimeOffset.UtcNow
                    ? "Active"
                    : subscription.IsApproved
                        ? "Expired"
                        : subscription.PaymentStatus == PaymentStatusEnum.Failed
                            ? "Failed"
                            : subscription.PaymentStatus == PaymentStatusEnum.Success
                                ? "Paid"
                                : "Pending approval";

            return new PendingSubscriptionDto
            {
                Id = subscription.Id,
                UserId = subscription.UserId,
                UserEmail = subscription.User?.Email ?? string.Empty,
                UserFullName = subscription.User?.FullName ?? string.Empty,
                SubscriptionPlanId = subscription.SubscriptionPlanId,
                PlanName = !string.IsNullOrWhiteSpace(subscription.PlanNameSnapshot)
                    ? subscription.PlanNameSnapshot
                    : subscription.SubscriptionPlan?.Name ?? string.Empty,
                PlanPrice = subscription.PlanPriceSnapshot,
                PlanDurationInDays = subscription.PlanDurationInDaysSnapshot,
                DurationMonths = Math.Max(1, (int)Math.Round(subscription.PlanDurationInDaysSnapshot / 30.4375m)),
                StartDate = subscription.StartDate,
                EndDate = subscription.EndDate,
                CreatedAt = subscription.CreatedAt,
                UpdatedAt = subscription.UpdatedAt,
                IsApproved = subscription.IsApproved,
                IsDeleted = subscription.IsDeleted,
                Status = status,
                PaymentStatus = subscription.PaymentStatus.ToString(),
                PaymentMethod = subscription.PaymentMethod?.ToString() ?? "Manual",
                PaymentReference = subscription.PaymentReference ?? string.Empty
            };
        }

        private decimal ConvertXafToUsd(decimal xafAmount)
        {
            var usdToXafRate = _configuration.GetValue<decimal?>("Subscriptions:UsdToXafRate") ?? 565m;
            if (usdToXafRate <= 0)
            {
                usdToXafRate = 565m;
            }

            return Math.Round(xafAmount / usdToXafRate, 2, MidpointRounding.AwayFromZero);
        }

        private long ConvertXafToStripeMinorUnits(decimal xafAmount)
        {
            var usdToXafRate = _configuration.GetValue<decimal?>("Subscriptions:UsdToXafRate") ?? 565m;
            if (usdToXafRate <= 0)
            {
                usdToXafRate = 565m;
            }

            var usdAmount = xafAmount / usdToXafRate;
            var cents = decimal.Round(usdAmount * 100m, 0, MidpointRounding.AwayFromZero);
            return Math.Max(50, decimal.ToInt64(cents));
        }

        private static JsonElement ResolveStripeEventObject(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("data", out var dataElement) &&
                dataElement.ValueKind == JsonValueKind.Object &&
                dataElement.TryGetProperty("object", out var objectElement))
            {
                return objectElement;
            }

            return root;
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

        private static PaymentMethodEnum? ResolveRegisteredPaymentMethod(ApplicationUser user)
        {
            var channel = user.SubscriptionPaymentChannel ?? user.PayoutChannel;
            return channel switch
            {
                PayoutChannelEnum.MtnMoney => PaymentMethodEnum.Momo,
                PayoutChannelEnum.OrangeMoney => PaymentMethodEnum.OrangeMoney,
                _ => null
            };
        }

        private static string? ResolveRegisteredMobileMoneyPhoneNumber(ApplicationUser user)
        {
            if (!string.IsNullOrWhiteSpace(user.SubscriptionPaymentPhoneNumber))
            {
                return user.SubscriptionPaymentPhoneNumber;
            }

            if (!string.IsNullOrWhiteSpace(user.PayoutPhoneNumber))
            {
                return user.PayoutPhoneNumber;
            }

            return user.PhoneNumber;
        }

        private static string BuildPaymentReference(int subscriptionId, int attemptCount)
        {
            return $"rhsub_{subscriptionId}_{attemptCount}";
        }

        private static bool IsStripePayoutSetupComplete(ApplicationUser user)
        {
            return !string.IsNullOrWhiteSpace(user.StripeConnectAccountId) &&
                   user.StripePayoutDetailsSubmitted &&
                   user.StripeChargesEnabled &&
                   user.StripePayoutsEnabled;
        }

        private bool IsStripePayoutSetupRequired()
        {
            var connectEnabled = _configuration.GetValue<bool?>("Stripe:Connect:Enabled").GetValueOrDefault(false);
            return _configuration.GetValue<bool?>("Stripe:Connect:RequirePayoutSetup") ?? connectEnabled;
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

        private static string? ReadDirectString(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

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
            }

            return null;
        }
    }
}
