using Common.Enums;
using Common.Helpers;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Models.Entities;

namespace RentHub.API.Helpers
{
    public static class TenancyLifecycleHelper
    {
        public static Task<bool> HasOverlappingTenancyAsync(
            ApplicationDbContext context,
            int apartmentId,
            DateTimeOffset startDate,
            DateTimeOffset? endDate,
            int? ignoredTenancyId = null,
            CancellationToken cancellationToken = default)
        {
            return context.Tenancies
                .IgnoreQueryFilters()
                .Where(tenancy =>
                    !tenancy.IsDeleted &&
                    tenancy.ApartmentId == apartmentId &&
                    (!ignoredTenancyId.HasValue || tenancy.Id != ignoredTenancyId.Value))
                .Where(tenancy => tenancy.TerminatedAt == null)
                .AnyAsync(tenancy =>
                    (endDate == null || tenancy.StartDate <= endDate.Value) &&
                    (tenancy.EndDate == null || startDate <= tenancy.EndDate.Value),
                    cancellationToken);
        }

        public static async Task SynchronizeRentPeriodsForExtensionAsync(
            ApplicationDbContext context,
            Tenancy tenancy,
            DateTimeOffset newEndDate,
            string userId,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            var scheduleEndBehavior = tenancy.EndBehavior == TenancyEndBehaviorEnum.NoEndDate
                ? TenancyEndBehaviorEnum.ExpireAutomatically
                : tenancy.EndBehavior;
            var generated = RentPeriodScheduleHelper.GeneratePeriods(
                tenancy.StartDate,
                newEndDate,
                scheduleEndBehavior,
                tenancy.MonthlyRent,
                tenancy.RentDueDay,
                nowUtc);

            var existing = await context.RentPeriods
                .Where(period => period.TenancyId == tenancy.Id && !period.IsDeleted)
                .ToDictionaryAsync(period => period.PeriodStart.Date, cancellationToken);

            foreach (var seed in generated.OrderBy(period => period.PeriodStart))
            {
                if (existing.TryGetValue(seed.PeriodStart.Date, out var period))
                {
                    if (seed.PeriodEnd.Date > period.PeriodEnd.Date)
                    {
                        period.PeriodEnd = seed.PeriodEnd;
                        period.UpdatedBy = userId;
                        period.UpdatedAt = nowUtc;
                    }

                    continue;
                }

                context.RentPeriods.Add(new RentPeriod
                {
                    TenancyId = tenancy.Id,
                    PeriodStart = seed.PeriodStart,
                    PeriodEnd = seed.PeriodEnd,
                    DueDate = seed.DueDate,
                    Amount = seed.Amount,
                    PaidAmount = 0,
                    Status = seed.Status,
                    CreatedBy = userId,
                    CreatedAt = nowUtc,
                    IsDeleted = false
                });
            }
        }
    }
}
