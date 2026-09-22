using Microsoft.EntityFrameworkCore;
using LontsiHomes.API.Data;

namespace LontsiHomes.API.Helpers
{
    public static class PaymentAvailabilityHelper
    {
        public const string AutomaticPaymentsUnavailableMessage =
            "Automatic payments are temporarily unavailable. Review the payment instructions and contact your landlord to arrange payment.";

        public const string SubscriptionRequiredMessage =
            "Your subscription must be paid and approved before you can add or create more items.";

        public static async Task<bool> IsPlatformAutomaticPaymentEnabledAsync(ApplicationDbContext context)
        {
            return await context.PlatformPaymentSettings
                .AsNoTracking()
                .Where(settings => settings.Id == 1)
                .Select(settings => settings.AutomaticPaymentsEnabled)
                .FirstOrDefaultAsync();
        }

        public static async Task<bool> IsAutomaticPaymentEnabledForPropertyAsync(
            ApplicationDbContext context,
            int propertyId)
        {
            if (!await IsPlatformAutomaticPaymentEnabledAsync(context))
            {
                return false;
            }

            return await context.Properties
                .AsNoTracking()
                .Where(property => property.Id == propertyId)
                .Select(property => property.AutomaticPaymentsEnabled)
                .FirstOrDefaultAsync();
        }

        public static async Task<bool> HasActiveSubscriptionAsync(ApplicationDbContext context, string landlordId)
        {
            if (await context.Users.AsNoTracking().AnyAsync(user =>
                    user.Id == landlordId && user.IsSubscriptionExempt))
            {
                return true;
            }

            var now = DateTimeOffset.UtcNow;
            return await context.UserSubscriptions.AsNoTracking().AnyAsync(subscription =>
                subscription.UserId == landlordId &&
                subscription.IsApproved &&
                subscription.PaymentStatus == Common.Enums.PaymentStatusEnum.Success &&
                subscription.EndDate > now);
        }
    }
}
