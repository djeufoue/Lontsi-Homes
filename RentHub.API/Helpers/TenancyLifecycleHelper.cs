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
                .AnyAsync(tenancy =>
                    (endDate == null || tenancy.StartDate <= endDate.Value) &&
                    ((tenancy.TerminatedAt ?? tenancy.EndDate) == null ||
                     startDate <= (tenancy.TerminatedAt ?? tenancy.EndDate)!.Value),
                    cancellationToken);
        }

        public static async Task ApplyApprovedTerminationAsync(
            ApplicationDbContext context,
            Tenancy tenancy,
            DateTimeOffset requestedEndDate,
            string userId,
            string? tenantReason,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            await ApplyTerminationAsync(
                context,
                tenancy,
                requestedEndDate,
                userId,
                TenancyTerminationReasonEnum.TenantRequest,
                tenantReason,
                nowUtc,
                cancellationToken);
        }

        public static async Task ApplyTerminationAsync(
            ApplicationDbContext context,
            Tenancy tenancy,
            DateTimeOffset requestedEndDate,
            string userId,
            TenancyTerminationReasonEnum reason,
            string? notes,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            var endDate = new DateTimeOffset(
                requestedEndDate.Year,
                requestedEndDate.Month,
                requestedEndDate.Day,
                0,
                0,
                0,
                TimeSpan.Zero);
            if (!tenancy.EndDate.HasValue && endDate >= tenancy.StartDate.Date)
            {
                await SynchronizeRentPeriodsForExtensionAsync(
                    context,
                    tenancy,
                    endDate,
                    userId,
                    nowUtc,
                    cancellationToken);
            }

            tenancy.EndDate = endDate;
            tenancy.EndBehavior = TenancyEndBehaviorEnum.ExpireAutomatically;
            tenancy.TerminatedAt = endDate;
            tenancy.TerminationReason = reason;
            tenancy.TerminationNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
            tenancy.TerminatedBy = userId;
            tenancy.RenewalReminderSentAt = null;
            tenancy.RenewalReminderSentForEndDate = null;
            tenancy.UpdatedBy = userId;
            tenancy.UpdatedAt = nowUtc;

            var affectedPeriods = await context.RentPeriods
                .Where(period =>
                    period.TenancyId == tenancy.Id &&
                    !period.IsDeleted &&
                    period.PeriodEnd.Date >= endDate.Date)
                .ToListAsync(cancellationToken);

            foreach (var period in affectedPeriods)
            {
                if (period.PeriodStart.Date >= endDate.Date)
                {
                    if (!RentPeriodScheduleHelper.IsPaidStatus(period.Status))
                    {
                        period.Status = RentPeriodStatusEnum.Cancelled;
                        period.UpdatedBy = userId;
                        period.UpdatedAt = nowUtc;
                    }
                }
                else if (period.PeriodEnd.Date >= endDate.Date &&
                         !RentPeriodScheduleHelper.IsPaidStatus(period.Status))
                {
                    period.PeriodEnd = endDate.AddDays(-1);
                    period.UpdatedBy = userId;
                    period.UpdatedAt = nowUtc;
                }
            }
        }

        public static async Task RestoreCurrentTenancyAsync(
            ApplicationDbContext context,
            Tenancy tenancy,
            DateTimeOffset? newEndDate,
            string userId,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            var normalizedEndDate = newEndDate.HasValue
                ? new DateTimeOffset(
                    newEndDate.Value.Year,
                    newEndDate.Value.Month,
                    newEndDate.Value.Day,
                    0,
                    0,
                    0,
                    TimeSpan.Zero)
                : (DateTimeOffset?)null;
            var defaultOpenEnd = LastDayOfMonth(nowUtc.AddMonths(1));
            var latestCancelledEnd = await context.RentPeriods
                .Where(period =>
                    period.TenancyId == tenancy.Id &&
                    !period.IsDeleted &&
                    period.Status == RentPeriodStatusEnum.Cancelled)
                .MaxAsync(period => (DateTimeOffset?)period.PeriodEnd, cancellationToken);
            var scheduleEnd = normalizedEndDate ??
                              (latestCancelledEnd.HasValue && latestCancelledEnd.Value.Date > defaultOpenEnd.Date
                                  ? latestCancelledEnd.Value
                                  : defaultOpenEnd);

            await SynchronizeRentPeriodsForExtensionAsync(
                context,
                tenancy,
                scheduleEnd,
                userId,
                nowUtc,
                cancellationToken);

            var cancelledPeriods = await context.RentPeriods
                .Where(period =>
                    period.TenancyId == tenancy.Id &&
                    !period.IsDeleted &&
                    period.Status == RentPeriodStatusEnum.Cancelled &&
                    period.PeriodStart.Date <= scheduleEnd.Date)
                .ToListAsync(cancellationToken);
            foreach (var period in cancelledPeriods)
            {
                period.Status = RentPeriodScheduleHelper.ResolveUnpaidStatus(period.DueDate, nowUtc);
                period.UpdatedBy = userId;
                period.UpdatedAt = nowUtc;
            }

            tenancy.EndDate = normalizedEndDate;
            tenancy.EndBehavior = normalizedEndDate.HasValue
                ? TenancyEndBehaviorEnum.ExpireAutomatically
                : TenancyEndBehaviorEnum.NoEndDate;
            tenancy.TerminatedAt = null;
            tenancy.TerminationReason = null;
            tenancy.TerminationNotes = null;
            tenancy.TerminatedBy = null;
            tenancy.RenewalReminderSentAt = null;
            tenancy.RenewalReminderSentForEndDate = null;
            tenancy.UpdatedBy = userId;
            tenancy.UpdatedAt = nowUtc;
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

        private static DateTimeOffset LastDayOfMonth(DateTimeOffset value)
        {
            var day = DateTime.DaysInMonth(value.Year, value.Month);
            return new DateTimeOffset(value.Year, value.Month, day, 0, 0, 0, TimeSpan.Zero);
        }
    }
}
