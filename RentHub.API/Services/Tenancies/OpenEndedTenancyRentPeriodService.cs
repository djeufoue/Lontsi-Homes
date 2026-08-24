using Common.Enums;
using Common.Helpers;
using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Models.Entities;

namespace RentHub.API.Services.Tenancies
{
    public interface IOpenEndedTenancyRentPeriodService
    {
        Task EnsureAllOpenEndedTenancyPeriodsAsync();

        Task<int> EnsureTenancyPeriodsAsync(
            int tenancyId,
            CancellationToken cancellationToken = default);
    }

    public sealed class OpenEndedTenancyRentPeriodService : IOpenEndedTenancyRentPeriodService
    {
        private const string SchedulerUser = "system:open-ended-tenancy-scheduler";

        private static readonly RentPeriodStatusEnum[] ClosedPeriodStatuses =
        {
            RentPeriodStatusEnum.Paid,
            RentPeriodStatusEnum.PaidBeforeRentHub,
            RentPeriodStatusEnum.PaidInAdvance,
            RentPeriodStatusEnum.Waived,
            RentPeriodStatusEnum.Cancelled
        };

        private readonly ApplicationDbContext _context;
        private readonly ILogger<OpenEndedTenancyRentPeriodService> _logger;

        public OpenEndedTenancyRentPeriodService(
            ApplicationDbContext context,
            ILogger<OpenEndedTenancyRentPeriodService> logger)
        {
            _context = context;
            _logger = logger;
        }

        [DisableConcurrentExecution(timeoutInSeconds: 600)]
        public async Task EnsureAllOpenEndedTenancyPeriodsAsync()
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var result = await EnsureTenancyPeriodsCoreAsync(
                tenancyId: null,
                nowUtc,
                CancellationToken.None);

            _logger.LogInformation(
                "Open-ended tenancy rent-period provisioning completed. Tenancies checked: {TenancyCount}; periods created: {PeriodCount}.",
                result.TenanciesChecked,
                result.PeriodsCreated);
        }

        public async Task<int> EnsureTenancyPeriodsAsync(
            int tenancyId,
            CancellationToken cancellationToken = default)
        {
            var result = await EnsureTenancyPeriodsCoreAsync(
                tenancyId,
                DateTimeOffset.UtcNow,
                cancellationToken);

            return result.PeriodsCreated;
        }

        private async Task<ProvisioningResult> EnsureTenancyPeriodsCoreAsync(
            int? tenancyId,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken,
            bool retryOnDuplicate = true)
        {
            var eligibleTenancies = _context.Tenancies
                .AsNoTracking()
                .Where(tenancy =>
                    tenancy.TerminatedAt == null &&
                    tenancy.StartDate <= nowUtc &&
                    (tenancy.EndBehavior == TenancyEndBehaviorEnum.NoEndDate ||
                     tenancy.EndBehavior == TenancyEndBehaviorEnum.ContinueMonthToMonth));

            if (tenancyId.HasValue)
            {
                eligibleTenancies = eligibleTenancies.Where(tenancy => tenancy.Id == tenancyId.Value);
            }

            // Project only the information needed to extend each schedule. EF turns
            // the latest-period and open-period checks into correlated SQL subqueries,
            // so the daily job uses one read instead of one read per tenancy.
            var schedules = await eligibleTenancies
                .Select(tenancy => new OpenEndedTenancySchedule
                {
                    TenancyId = tenancy.Id,
                    StartDate = tenancy.StartDate,
                    MonthlyRent = tenancy.MonthlyRent,
                    RentDueDay = tenancy.RentDueDay,
                    FutureRentPeriodCount = tenancy.FutureRentPeriodCount,
                    PaymentIntervalMonths = tenancy.PaymentIntervalMonths,
                    RentTrackingStartDate = tenancy.RentTrackingStartDate,
                    RentScheduleNeedsReview = tenancy.RentScheduleNeedsReview,
                    LatestPeriodEnd = tenancy.RentPeriods
                        .OrderByDescending(period => period.PeriodStart)
                        .Select(period => (DateTimeOffset?)period.PeriodEnd)
                        .FirstOrDefault(),
                    HasOpenPeriod = tenancy.RentPeriods.Any(period =>
                        !ClosedPeriodStatuses.Contains(period.Status))
                })
                .ToListAsync(cancellationToken);

            // Rent periods retain the tenancy's original UTC offset. Compare their
            // calendar dates after the single database read so +01:00 and -05:00
            // schedules ending on the same day are not treated as different instants.
            var schedulesToExtend = schedules
                .Where(schedule => !schedule.RentScheduleNeedsReview)
                .ToList();

            var newPeriods = new List<RentPeriod>();
            foreach (var schedule in schedulesToExtend)
            {
                var generationAsOf = !schedule.HasOpenPeriod &&
                                     schedule.LatestPeriodEnd.HasValue &&
                                     schedule.LatestPeriodEnd.Value.Date > nowUtc.Date
                    ? schedule.LatestPeriodEnd.Value.AddDays(1)
                    : nowUtc;
                var seeds = RentPeriodScheduleHelper.GeneratePeriods(
                    schedule.StartDate,
                    null,
                    TenancyEndBehaviorEnum.NoEndDate,
                    schedule.MonthlyRent,
                    schedule.RentDueDay,
                    generationAsOf,
                    schedule.FutureRentPeriodCount,
                    schedule.PaymentIntervalMonths,
                    schedule.RentTrackingStartDate == default
                        ? schedule.StartDate
                        : schedule.RentTrackingStartDate);

                newPeriods.AddRange(seeds
                    .Where(seed => !schedule.LatestPeriodEnd.HasValue ||
                                   seed.PeriodStart.Date > schedule.LatestPeriodEnd.Value.Date)
                    .Select(seed => new RentPeriod
                    {
                        TenancyId = schedule.TenancyId,
                        PeriodStart = seed.PeriodStart,
                        PeriodEnd = seed.PeriodEnd,
                        DueDate = seed.DueDate,
                        BillingGroupSequence = seed.BillingGroupSequence,
                        Amount = seed.Amount,
                        PaidAmount = 0m,
                        PaymentReference = string.Empty,
                        Status = RentPeriodScheduleHelper.ResolveUnpaidStatus(seed.DueDate, nowUtc),
                        CreatedBy = SchedulerUser,
                        CreatedAt = nowUtc
                    }));
            }

            if (newPeriods.Count == 0)
            {
                return new ProvisioningResult(schedules.Count, 0);
            }

            await _context.RentPeriods.AddRangeAsync(newPeriods, cancellationToken);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (
                retryOnDuplicate &&
                exception.GetBaseException() is SqlException { Number: 2601 or 2627 })
            {
                // A payment request, the startup catch-up, and the recurring job
                // may legitimately overlap. The database uniqueness constraint on
                // (TenancyId, PeriodStart) remains the final concurrency guard.
                _logger.LogInformation(
                    "Another execution provisioned open-ended tenancy rent periods; rechecking the affected schedule. Tenancy: {TenancyId}.",
                    tenancyId);

                _context.ChangeTracker.Clear();
                return await EnsureTenancyPeriodsCoreAsync(
                    tenancyId,
                    nowUtc,
                    cancellationToken,
                    retryOnDuplicate: false);
            }

            _logger.LogInformation(
                "Created {PeriodCount} rent period(s) for {TenancyCount} open-ended tenancy schedule(s), through at least {TargetEnd:yyyy-MM-dd}.",
                newPeriods.Count,
                schedulesToExtend.Count,
                schedulesToExtend.Count == 0
                    ? nowUtc
                    : newPeriods.Max(period => period.PeriodEnd));

            return new ProvisioningResult(schedules.Count, newPeriods.Count);
        }

        private sealed class OpenEndedTenancySchedule
        {
            public int TenancyId { get; init; }
            public DateTimeOffset StartDate { get; init; }
            public decimal MonthlyRent { get; init; }
            public int RentDueDay { get; init; }
            public int FutureRentPeriodCount { get; init; }
            public int PaymentIntervalMonths { get; init; }
            public DateTimeOffset RentTrackingStartDate { get; init; }
            public bool RentScheduleNeedsReview { get; init; }
            public DateTimeOffset? LatestPeriodEnd { get; init; }
            public bool HasOpenPeriod { get; init; }
        }

        private readonly record struct ProvisioningResult(
            int TenanciesChecked,
            int PeriodsCreated);
    }
}
