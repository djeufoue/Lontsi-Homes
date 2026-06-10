using Common.Enums;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Payments;

namespace RentHub.API.Services.Subscriptions
{
    public interface ISubscriptionAutoRenewalService
    {
        Task<int> ProcessDueRenewalsAsync();
    }

    public class SubscriptionAutoRenewalService : ISubscriptionAutoRenewalService
    {
        private readonly ApplicationDbContext _context;
        private readonly IStripeCheckoutService _stripeCheckoutService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<SubscriptionAutoRenewalService> _logger;

        public SubscriptionAutoRenewalService(
            ApplicationDbContext context,
            IStripeCheckoutService stripeCheckoutService,
            IConfiguration configuration,
            ILogger<SubscriptionAutoRenewalService> logger)
        {
            _context = context;
            _stripeCheckoutService = stripeCheckoutService;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<int> ProcessDueRenewalsAsync()
        {
            var now = DateTimeOffset.UtcNow;
            var retryAfterHours = Math.Max(1, _configuration.GetValue<int?>("Subscriptions:AutoRenewalRetryHours") ?? 12);
            var retryCutoff = now.AddHours(-retryAfterHours);
            var processedCount = 0;
            var processedUsers = new HashSet<string>(StringComparer.Ordinal);

            var candidates = await _context.UserSubscriptions
                .Include(subscription => subscription.User)
                .Include(subscription => subscription.SubscriptionPlan)
                .Where(subscription =>
                    !subscription.IsDeleted &&
                    subscription.IsApproved &&
                    subscription.PaymentStatus == PaymentStatusEnum.Success &&
                    subscription.PaymentMethod == PaymentMethodEnum.Card &&
                    subscription.AllowAutomaticCardPayments &&
                    subscription.EndDate <= now &&
                    !string.IsNullOrWhiteSpace(subscription.StripeCustomerId) &&
                    !string.IsNullOrWhiteSpace(subscription.StripePaymentMethodId) &&
                    (subscription.LastAutomaticPaymentAttemptAt == null ||
                     subscription.LastAutomaticPaymentAttemptAt <= retryCutoff))
                .OrderBy(subscription => subscription.EndDate)
                .Take(50)
                .ToListAsync();

            foreach (var expiredSubscription in candidates)
            {
                if (!processedUsers.Add(expiredSubscription.UserId))
                {
                    continue;
                }

                if (expiredSubscription.User == null || expiredSubscription.SubscriptionPlan == null)
                {
                    continue;
                }

                var hasNewerSubscription = await _context.UserSubscriptions.AnyAsync(subscription =>
                    subscription.UserId == expiredSubscription.UserId &&
                    subscription.Id != expiredSubscription.Id &&
                    !subscription.IsDeleted &&
                    subscription.IsApproved &&
                    subscription.PaymentStatus == PaymentStatusEnum.Success &&
                    subscription.EndDate > expiredSubscription.EndDate);

                if (hasNewerSubscription)
                {
                    continue;
                }

                expiredSubscription.LastAutomaticPaymentAttemptAt = now;
                expiredSubscription.UpdatedBy = "system-auto-renewal";
                expiredSubscription.UpdatedAt = now;

                var renewal = await _context.UserSubscriptions
                    .FirstOrDefaultAsync(subscription =>
                        subscription.UserId == expiredSubscription.UserId &&
                        subscription.SubscriptionPlanId == expiredSubscription.SubscriptionPlanId &&
                        !subscription.IsDeleted &&
                        !subscription.IsApproved &&
                        subscription.PaymentStatus != PaymentStatusEnum.Success &&
                        subscription.IsAutomaticRenewal);

                if (renewal == null)
                {
                    renewal = new UserSubscription
                    {
                        UserId = expiredSubscription.UserId,
                        SubscriptionPlanId = expiredSubscription.SubscriptionPlanId,
                        CreatedBy = "system-auto-renewal",
                        CreatedAt = now,
                        IsAutomaticRenewal = true,
                        IsDeleted = false
                    };
                    _context.UserSubscriptions.Add(renewal);
                    await _context.SaveChangesAsync();
                }

                renewal.StartDate = now;
                renewal.EndDate = now.AddDays(expiredSubscription.PlanDurationInDaysSnapshot > 0
                    ? expiredSubscription.PlanDurationInDaysSnapshot
                    : expiredSubscription.SubscriptionPlan.DurationInDays);
                renewal.PlanNameSnapshot = !string.IsNullOrWhiteSpace(expiredSubscription.PlanNameSnapshot)
                    ? expiredSubscription.PlanNameSnapshot
                    : expiredSubscription.SubscriptionPlan.Name;
                renewal.PlanPriceSnapshot = expiredSubscription.PlanPriceSnapshot > 0
                    ? expiredSubscription.PlanPriceSnapshot
                    : expiredSubscription.SubscriptionPlan.Price;
                renewal.PlanDurationInDaysSnapshot = expiredSubscription.PlanDurationInDaysSnapshot > 0
                    ? expiredSubscription.PlanDurationInDaysSnapshot
                    : expiredSubscription.SubscriptionPlan.DurationInDays;
                renewal.PlanMaxPropertiesSnapshot = expiredSubscription.PlanMaxPropertiesSnapshot;
                renewal.PlanMaxApartmentsPerPropertySnapshot = expiredSubscription.PlanMaxApartmentsPerPropertySnapshot;
                renewal.PaymentMethod = PaymentMethodEnum.Card;
                renewal.AllowAutomaticCardPayments = true;
                renewal.StripeCustomerId = expiredSubscription.StripeCustomerId;
                renewal.StripePaymentMethodId = expiredSubscription.StripePaymentMethodId;
                renewal.PaymentStatus = PaymentStatusEnum.Pending;
                renewal.PaymentCompletedAt = null;
                renewal.PaymentProviderTransactionId = null;
                renewal.PaymentAuthorizationUrl = null;
                renewal.IsApproved = false;
                renewal.AutomaticPaymentFailureReason = null;
                renewal.LastAutomaticPaymentAttemptAt = now;
                renewal.PaymentAttemptCount += 1;
                renewal.PaymentReference = BuildPaymentReference(renewal.Id, renewal.PaymentAttemptCount);
                renewal.UpdatedBy = "system-auto-renewal";
                renewal.UpdatedAt = now;

                await _context.SaveChangesAsync();

                var amountInMinorUnits = ConvertXafToStripeMinorUnits(renewal.PlanPriceSnapshot);
                var result = await _stripeCheckoutService.CreateAutomaticSubscriptionPaymentAsync(
                    expiredSubscription.User,
                    expiredSubscription.SubscriptionPlan,
                    renewal,
                    amountInMinorUnits,
                    ResolveStripeCurrency(),
                    renewal.StripeCustomerId ?? string.Empty,
                    renewal.StripePaymentMethodId ?? string.Empty);

                renewal.PaymentProviderTransactionId = string.IsNullOrWhiteSpace(result.PaymentIntentId)
                    ? renewal.PaymentProviderTransactionId
                    : result.PaymentIntentId;
                renewal.StripeCustomerId = string.IsNullOrWhiteSpace(result.CustomerId)
                    ? renewal.StripeCustomerId
                    : result.CustomerId;
                renewal.StripePaymentMethodId = string.IsNullOrWhiteSpace(result.PaymentMethodId)
                    ? renewal.StripePaymentMethodId
                    : result.PaymentMethodId;
                renewal.UpdatedBy = "system-auto-renewal";
                renewal.UpdatedAt = DateTimeOffset.UtcNow;

                if (string.Equals(result.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    renewal.PaymentStatus = PaymentStatusEnum.Success;
                    renewal.PaymentCompletedAt = renewal.UpdatedAt;
                    renewal.IsApproved = true;

                    await EndOverlappingSubscriptionsAsync(renewal, renewal.UpdatedAt.Value);
                    _logger.LogInformation(
                        "Automatic subscription renewal succeeded for {UserEmail}. SubscriptionId={SubscriptionId}, PaymentReference={PaymentReference}",
                        expiredSubscription.User.Email,
                        renewal.Id,
                        renewal.PaymentReference);
                }
                else
                {
                    renewal.PaymentStatus = PaymentStatusEnum.Failed;
                    renewal.IsApproved = false;
                    renewal.AutomaticPaymentFailureReason = string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? $"Stripe returned status '{result.Status}'. The landlord may need to pay manually."
                        : result.ErrorMessage;

                    _logger.LogWarning(
                        "Automatic subscription renewal failed for {UserEmail}. SubscriptionId={SubscriptionId}, PaymentReference={PaymentReference}, Status={Status}, Reason={Reason}",
                        expiredSubscription.User.Email,
                        renewal.Id,
                        renewal.PaymentReference,
                        result.Status,
                        renewal.AutomaticPaymentFailureReason);
                }

                await _context.SaveChangesAsync();
                processedCount++;
            }

            return processedCount;
        }

        private async Task EndOverlappingSubscriptionsAsync(UserSubscription renewal, DateTimeOffset now)
        {
            var overlapping = await _context.UserSubscriptions
                .Where(subscription =>
                    subscription.UserId == renewal.UserId &&
                    subscription.Id != renewal.Id &&
                    !subscription.IsDeleted &&
                    subscription.IsApproved &&
                    subscription.EndDate > now)
                .ToListAsync();

            foreach (var subscription in overlapping)
            {
                subscription.EndDate = now;
                subscription.UpdatedBy = "system-auto-renewal";
                subscription.UpdatedAt = now;
            }
        }

        private string ResolveStripeCurrency()
        {
            var currency = (_configuration["Stripe:Currency"] ?? "usd").Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(currency) ? "usd" : currency;
        }

        private long ConvertXafToStripeMinorUnits(decimal amountXaf)
        {
            var rate = _configuration.GetValue<decimal?>("Subscriptions:UsdToXafRate") ?? 565m;
            if (rate <= 0)
            {
                rate = 565m;
            }

            var amountUsd = Math.Round(amountXaf / rate, 2, MidpointRounding.AwayFromZero);
            return Math.Max(50, (long)Math.Round(amountUsd * 100m, 0, MidpointRounding.AwayFromZero));
        }

        private static string BuildPaymentReference(int subscriptionId, int attempt)
        {
            return $"rhsub_{subscriptionId}_{Math.Max(1, attempt)}";
        }
    }
}
