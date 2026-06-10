using Common.CommunicationModels;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Models.Entities;

namespace RentHub.API.Helpers
{
    public static class PropertyHelpers
    {
        public static async Task<PropertyCreationScopeDto> BuildCreationScopeAsync(
            ApplicationDbContext context,
            string landlordId,
            string landlordName)
        {
            var now = DateTimeOffset.UtcNow;
            var landlord = await context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == landlordId);

            var kycProfile = await context.LandlordKycProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == landlordId);

            var activeSubscription = await context.UserSubscriptions
                .Include(us => us.SubscriptionPlan)
                .Where(us => us.UserId == landlordId && us.IsApproved && us.EndDate > now)
                .OrderByDescending(us => us.EndDate)
                .FirstOrDefaultAsync();

            var currentCount = await context.Properties.CountAsync(p => p.LandlordId == landlordId);
            var maxProps = activeSubscription != null
                ? (activeSubscription.PlanMaxPropertiesSnapshot ?? activeSubscription.SubscriptionPlan?.MaxProperties)
                : null;

            var scope = new PropertyCreationScopeDto
            {
                LandlordId = landlordId,
                LandlordName = landlordName,
                CurrentProperties = currentCount,
                MaxProperties = maxProps,
                SubscriptionApproved = activeSubscription?.IsApproved == true,
                KycApproved = kycProfile?.Status == LandlordKycStatusEnum.Approved,
                PlatformTermsAccepted = landlord?.PlatformTermsAccepted == true,
                StripePayoutSetupComplete = landlord != null && IsStripePayoutSetupComplete(landlord),
                CanCreate = false,
                StatusMessage = "No active subscription found."
            };

            if (kycProfile == null)
            {
                scope.StatusMessage = "Identity verification must be submitted before properties can be created.";
                return scope;
            }

            if (kycProfile.Status == LandlordKycStatusEnum.Rejected)
            {
                scope.StatusMessage = string.IsNullOrWhiteSpace(kycProfile.ReviewNote)
                    ? "Identity verification was rejected. Submit corrected documents."
                    : kycProfile.ReviewNote;
                return scope;
            }

            if (kycProfile.Status != LandlordKycStatusEnum.Approved)
            {
                scope.StatusMessage = "Identity verification is waiting for admin approval.";
                return scope;
            }

            if (landlord?.PlatformTermsAccepted != true)
            {
                scope.StatusMessage = "Platform contract must be signed before properties can be created.";
                return scope;
            }

            if (!scope.StripePayoutSetupComplete)
            {
                scope.StatusMessage = "Set up your payout account before creating properties so tenant rent payments can be routed to you later.";
                return scope;
            }

            if (activeSubscription == null)
                return scope;

            if (!activeSubscription.IsApproved)
            {
                scope.StatusMessage = activeSubscription.PaymentStatus == PaymentStatusEnum.Success
                    ? "Subscription is awaiting final approval."
                    : "Subscription payment is pending.";
                return scope;
            }

            if (maxProps.HasValue && currentCount >= maxProps.Value)
            {
                scope.StatusMessage = $"Maximum properties ({maxProps.Value}) reached for this subscription.";
                return scope;
            }

            scope.CanCreate = true;
            scope.StatusMessage = "Creation available.";
            return scope;
        }

        private static bool IsStripePayoutSetupComplete(ApplicationUser landlord)
        {
            return !string.IsNullOrWhiteSpace(landlord.StripeConnectAccountId) &&
                   landlord.StripePayoutDetailsSubmitted &&
                   landlord.StripeChargesEnabled &&
                   landlord.StripePayoutsEnabled;
        }

        public static async Task<bool> CanAccessPropertyAsync(
            ApplicationDbContext context,
            int propertyId,
            string userId,
            bool isAdmin)
        {
            if (isAdmin) return true;

            var isLandlord = await context.Properties.AnyAsync(p => p.Id == propertyId && p.LandlordId == userId);
            if (isLandlord) return true;

            var isManager = await context.PropertyManagerAssignments.AnyAsync(m => m.PropertyId == propertyId && m.ManagerId == userId);
            if (isManager) return true;

            var isOwner = await context.ApartmentOwners.AnyAsync(o => o.OwnerId == userId && o.Apartment!.PropertyId == propertyId);
            if (isOwner) return true;

            return await context.Tenancies.AnyAsync(t =>
                t.Apartment!.PropertyId == propertyId &&
                t.Members.Any(mm => !mm.IsDeleted && mm.MemberId == userId));
        }

        public static async Task<bool> CanWritePropertyAsync(
            ApplicationDbContext context,
            int propertyId,
            string userId,
            bool isAdmin)
        {
            if (isAdmin) return true;

            var isLandlord = await context.Properties.AnyAsync(p => p.Id == propertyId && p.LandlordId == userId);
            if (isLandlord) return true;

            return await context.PropertyManagerAssignments.AnyAsync(m =>
                m.PropertyId == propertyId &&
                m.ManagerId == userId &&
                m.Permission == PermissionLevelEnum.ReadWrite);
        }

        public static string ResolvePrimaryRole(ISet<string> roles)
        {
            if (roles.Contains("Admin")) return "Admin";
            if (roles.Contains("Landlord")) return "Landlord";
            if (roles.Contains("Manager")) return "Manager";
            if (roles.Contains("Owner")) return "Owner";
            if (roles.Contains("Tenant")) return "Tenant";
            return "User";
        }

        public static string ResolveAccessSource(bool isAdmin, bool isOwned, bool isManaged, bool isOwner, bool isTenant)
        {
            if (isAdmin) return "Admin";
            if (isOwned) return "Owned";
            if (isManaged) return "Managed";
            if (isOwner) return "Owner";
            if (isTenant) return "Tenant";
            return "Member";
        }
    }
}





