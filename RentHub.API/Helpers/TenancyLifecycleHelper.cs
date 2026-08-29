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
                nowUtc,
                tenancy.PaymentIntervalMonths,
                tenancy.RentTrackingStartDate == default ? tenancy.StartDate : tenancy.RentTrackingStartDate);

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

                    period.DueDate = seed.DueDate;
                    period.BillingGroupSequence = seed.BillingGroupSequence;

                    continue;
                }

                context.RentPeriods.Add(new RentPeriod
                {
                    TenancyId = tenancy.Id,
                    PeriodStart = seed.PeriodStart,
                    PeriodEnd = seed.PeriodEnd,
                    DueDate = seed.DueDate,
                    BillingGroupSequence = seed.BillingGroupSequence,
                    Amount = seed.Amount,
                    PaidAmount = 0,
                    Status = seed.Status,
                    CreatedBy = userId,
                    CreatedAt = nowUtc,
                    IsDeleted = false
                });
            }
        }

        public static async Task ReconcileRentScheduleAsync(
            ApplicationDbContext context,
            Tenancy tenancy,
            string userId,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            var generated = RentPeriodScheduleHelper.GeneratePeriods(
                tenancy.StartDate,
                tenancy.EndDate,
                tenancy.EndBehavior,
                tenancy.MonthlyRent,
                tenancy.RentDueDay,
                nowUtc,
                tenancy.PaymentIntervalMonths,
                tenancy.RentTrackingStartDate == default ? tenancy.StartDate : tenancy.RentTrackingStartDate)
                .OrderBy(period => period.PeriodStart)
                .ToList();

            var existing = await context.RentPeriods
                .Where(period => period.TenancyId == tenancy.Id)
                .OrderBy(period => period.PeriodStart)
                .ToListAsync(cancellationToken);

            if (existing.Any(period => period.PaymentId.HasValue ||
                                       period.Status == RentPeriodStatusEnum.PendingPayment))
            {
                throw new InvalidOperationException(
                    "The rent schedule cannot be structurally changed while it contains real or pending payments.");
            }

            var existingByStart = existing.ToDictionary(period => period.PeriodStart.Date);
            var retainedPeriodIds = new HashSet<int>();
            foreach (var seed in generated)
            {
                if (!existingByStart.TryGetValue(seed.PeriodStart.Date, out var period))
                {
                    context.RentPeriods.Add(new RentPeriod
                    {
                        TenancyId = tenancy.Id,
                        PeriodStart = seed.PeriodStart,
                        PeriodEnd = seed.PeriodEnd,
                        DueDate = seed.DueDate,
                        BillingGroupSequence = seed.BillingGroupSequence,
                        Amount = seed.Amount,
                        PaidAmount = 0,
                        Status = seed.Status,
                        CreatedBy = userId,
                        CreatedAt = nowUtc
                    });
                    continue;
                }

                retainedPeriodIds.Add(period.Id);
                // A soft-deleted historical period is an explicit suppression marker.
                // Keep it archived so a later schedule reconciliation cannot recreate
                // history the landlord deliberately removed.
                if (period.IsDeleted)
                {
                    continue;
                }

                period.PeriodStart = seed.PeriodStart;
                period.PeriodEnd = seed.PeriodEnd;
                period.DueDate = seed.DueDate;
                period.BillingGroupSequence = seed.BillingGroupSequence;
                period.Amount = seed.Amount;
                if (period.Status is not RentPeriodStatusEnum.Waived and not RentPeriodStatusEnum.Cancelled)
                {
                    period.Status = RentPeriodScheduleHelper.ResolveUnpaidStatus(seed.DueDate, nowUtc);
                    period.PaidAmount = 0;
                    period.PaidDate = null;
                    period.PaymentReference = string.Empty;
                }

                period.UpdatedBy = userId;
                period.UpdatedAt = nowUtc;
            }

            foreach (var period in existing.Where(period =>
                         !period.IsDeleted && !retainedPeriodIds.Contains(period.Id)))
            {
                period.IsDeleted = true;
                period.DeletedBy = userId;
                period.DeletedAt = nowUtc;
                period.UpdatedBy = userId;
                period.UpdatedAt = nowUtc;
            }

            tenancy.RentScheduleNeedsReview = false;
            tenancy.UpdatedBy = userId;
            tenancy.UpdatedAt = nowUtc;
        }

        public static async Task InvalidateRentRemindersAsync(
            ApplicationDbContext context,
            int tenancyId,
            DateTimeOffset nowUtc,
            string reason,
            CancellationToken cancellationToken = default)
        {
            var reminders = await context.RentReminders
                .Where(reminder => reminder.TenancyId == tenancyId && !reminder.InvalidatedAt.HasValue)
                .ToListAsync(cancellationToken);
            foreach (var reminder in reminders)
            {
                reminder.InvalidatedAt = nowUtc;
                reminder.InvalidationReason = reason.Length <= 512 ? reason : reason[..512];
                reminder.UpdatedAt = nowUtc;
                if (reminder.Status is RentReminderStatusEnum.Pending or RentReminderStatusEnum.Failed)
                {
                    reminder.Status = RentReminderStatusEnum.Cancelled;
                    if (reminder.EmailStatus == ReminderDeliveryStatusEnum.Pending)
                        reminder.EmailStatus = ReminderDeliveryStatusEnum.Skipped;
                    if (reminder.SmsStatus == ReminderDeliveryStatusEnum.Pending)
                        reminder.SmsStatus = ReminderDeliveryStatusEnum.Skipped;
                }
            }
        }

        private static DateTimeOffset LastDayOfMonth(DateTimeOffset value)
        {
            var day = DateTime.DaysInMonth(value.Year, value.Month);
            return new DateTimeOffset(value.Year, value.Month, day, 0, 0, 0, TimeSpan.Zero);
        }
    }
}
