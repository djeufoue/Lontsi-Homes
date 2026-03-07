using Common.CommunicationModels;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;

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
            var activeSubscription = await context.UserSubscriptions
                .Include(us => us.SubscriptionPlan)
                .Where(us => us.UserId == landlordId && us.EndDate > now)
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
                CanCreate = false,
                StatusMessage = "No active subscription found."
            };

            if (activeSubscription == null)
                return scope;

            if (!activeSubscription.IsApproved)
            {
                scope.StatusMessage = "Subscription is pending approval.";
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
                (t.TenantId == userId || t.Members.Any(mm => mm.MemberId == userId)));
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


