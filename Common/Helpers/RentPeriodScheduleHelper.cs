using System;
using System.Collections.Generic;
using System.Linq;
using Common.CommunicationModels;
using Common.Enums;

namespace Common.Helpers
{
    public static class RentPeriodScheduleHelper
    {
        public static List<RentPeriodSeedDto> GeneratePeriods(
            DateTimeOffset startDate,
            DateTimeOffset? endDate,
            TenancyEndBehaviorEnum endBehavior,
            decimal monthlyRent,
            int rentDueDay,
            DateTimeOffset nowUtc,
            int paymentIntervalMonths = 1,
            DateTimeOffset? trackingStartDate = null)
        {
            var periods = new List<RentPeriodSeedDto>();
            if (monthlyRent <= 0)
            {
                return periods;
            }

            var normalizedDueDay = Math.Clamp(rentDueDay, 1, 31);
            var normalizedPaymentInterval = Math.Clamp(paymentIntervalMonths, 1, 12);
            var normalizedStart = startDate.Date;
            var normalizedTrackingStart = trackingStartDate?.Date ?? normalizedStart;
            if (normalizedTrackingStart < normalizedStart)
            {
                normalizedTrackingStart = normalizedStart;
            }

            var firstPeriodIndex = FindPeriodIndexContainingOrAfter(startDate, normalizedTrackingStart);
            var firstBoundary = MonthlyBoundary(startDate, firstPeriodIndex);
            if (firstBoundary.Date < normalizedTrackingStart)
            {
                firstPeriodIndex++;
            }

            var hardEnd = ResolveGenerationEnd(
                startDate,
                endDate,
                endBehavior,
                nowUtc,
                normalizedPaymentInterval,
                firstPeriodIndex).Date;

            for (var periodIndex = firstPeriodIndex;
                 periods.Count < 240;
                 periodIndex++)
            {
                var periodStart = MonthlyBoundary(startDate, periodIndex);
                if (periodStart.Date > hardEnd)
                {
                    break;
                }

                var naturalEnd = MonthlyBoundary(startDate, periodIndex + 1).AddDays(-1);
                var periodEnd = naturalEnd.Date > hardEnd
                    ? AtDate(hardEnd, startDate.Offset)
                    : naturalEnd;

                if (periodEnd.Date < periodStart.Date)
                {
                    break;
                }

                var billingGroupSequence = periodIndex / normalizedPaymentInterval;
                var groupFirstPeriodIndex = billingGroupSequence * normalizedPaymentInterval;
                var groupStart = MonthlyBoundary(startDate, groupFirstPeriodIndex);
                var groupEnd = MonthlyBoundary(startDate, groupFirstPeriodIndex + normalizedPaymentInterval).AddDays(-1);
                if (endDate.HasValue && groupEnd.Date > endDate.Value.Date)
                {
                    groupEnd = AtDate(endDate.Value.Date, startDate.Offset);
                }

                var dueDate = ResolveGroupDueDate(groupStart, groupEnd, normalizedDueDay);
                periods.Add(new RentPeriodSeedDto
                {
                    PeriodStart = periodStart,
                    PeriodEnd = periodEnd,
                    DueDate = dueDate,
                    BillingGroupSequence = billingGroupSequence,
                    Amount = monthlyRent,
                    Status = ResolveUnpaidStatus(dueDate, nowUtc),
                    PaidAmount = 0
                });
            }

            return periods;
        }

        public static DateTimeOffset ResolveExtensionReferenceDate(
            DateTimeOffset nowUtc,
            DateTimeOffset? latestPeriodEnd,
            bool hasOpenPeriod)
        {
            // GeneratePeriods already includes the next complete payment group.
            // Stay in the last paid group when payment is ahead of the calendar;
            // advancing to the following day would provision two more groups.
            return !hasOpenPeriod && latestPeriodEnd.HasValue && latestPeriodEnd.Value.Date > nowUtc.Date
                ? latestPeriodEnd.Value
                : nowUtc;
        }

        public static DateTimeOffset MonthlyBoundary(DateTimeOffset tenancyStart, int periodIndex)
        {
            if (periodIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(periodIndex));
            }

            var targetMonth = new DateTimeOffset(
                    tenancyStart.Year,
                    tenancyStart.Month,
                    1,
                    0,
                    0,
                    0,
                    tenancyStart.Offset)
                .AddMonths(periodIndex);
            var day = Math.Min(tenancyStart.Day, DateTime.DaysInMonth(targetMonth.Year, targetMonth.Month));
            return new DateTimeOffset(
                targetMonth.Year,
                targetMonth.Month,
                day,
                0,
                0,
                0,
                tenancyStart.Offset);
        }

        public static DateTimeOffset ResolveNextBillingGroupStart(
            DateTimeOffset tenancyStart,
            DateTimeOffset asOf,
            int paymentIntervalMonths)
        {
            var interval = Math.Clamp(paymentIntervalMonths, 1, 12);
            var currentIndex = FindPeriodIndexContainingOrAfter(tenancyStart, asOf.Date);
            var currentGroup = currentIndex / interval;
            var currentGroupStart = MonthlyBoundary(tenancyStart, currentGroup * interval);
            return currentGroupStart.Date > asOf.Date
                ? currentGroupStart
                : MonthlyBoundary(tenancyStart, (currentGroup + 1) * interval);
        }

        public static bool IsMonthlyBoundary(DateTimeOffset tenancyStart, DateTimeOffset candidate)
        {
            var index = FindPeriodIndexContainingOrAfter(tenancyStart, candidate.Date);
            return MonthlyBoundary(tenancyStart, index).Date == candidate.Date;
        }

        public static void ApplyImportMode(
            IList<RentPeriodSeedDto> periods,
            RentPaymentImportModeEnum importMode,
            DateTimeOffset nowUtc,
            DateTimeOffset? unpaidFrom = null,
            DateTimeOffset? unpaidTo = null,
            DateTimeOffset? paidInAdvanceFrom = null,
            DateTimeOffset? paidInAdvanceTo = null)
        {
            foreach (var period in periods)
            {
                period.Status = ResolveUnpaidStatus(period.DueDate, nowUtc);
                period.PaidAmount = 0;
                period.PaidDate = null;
            }

            if (importMode == RentPaymentImportModeEnum.AllPastPeriodsPaidBeforeRentHub)
            {
                foreach (var period in periods.Where(period => period.PeriodEnd.Date < nowUtc.Date))
                {
                    MarkPaid(period, RentPeriodStatusEnum.PaidBeforeRentHub, null);
                }
            }
            else if (importMode == RentPaymentImportModeEnum.SomePeriodsWerePaid)
            {
                if (!unpaidFrom.HasValue || !unpaidTo.HasValue)
                {
                    return;
                }

                foreach (var period in periods.Where(period => period.PeriodEnd.Date < unpaidFrom.Value.Date))
                {
                    MarkPaid(period, RentPeriodStatusEnum.PaidBeforeRentHub, null);
                }

                foreach (var period in periods.Where(period =>
                             period.PeriodStart.Date >= unpaidFrom.Value.Date &&
                             period.PeriodEnd.Date <= unpaidTo.Value.Date))
                {
                    period.Status = ResolveUnpaidStatus(period.DueDate, nowUtc);
                    period.PaidAmount = 0;
                    period.PaidDate = null;
                }
            }

            if (importMode == RentPaymentImportModeEnum.TenantPaidInAdvance ||
                paidInAdvanceFrom.HasValue ||
                paidInAdvanceTo.HasValue)
            {
                if (!paidInAdvanceFrom.HasValue || !paidInAdvanceTo.HasValue)
                {
                    return;
                }

                foreach (var period in periods.Where(period =>
                             period.PeriodStart.Date >= paidInAdvanceFrom.Value.Date &&
                             period.PeriodEnd.Date <= paidInAdvanceTo.Value.Date &&
                             period.DueDate.Date > nowUtc.Date))
                {
                    MarkPaid(period, RentPeriodStatusEnum.PaidInAdvance, paidInAdvanceFrom.Value);
                }
            }
        }

        public static List<string> ValidateContiguousRange(
            IEnumerable<RentPeriodSeedDto> periods,
            DateTimeOffset rangeStart,
            DateTimeOffset rangeEnd,
            string label)
        {
            var errors = new List<string>();
            var ordered = periods.OrderBy(period => period.PeriodStart).ToList();
            if (rangeEnd.Date < rangeStart.Date)
            {
                errors.Add($"{label} end date cannot be before the start date.");
                return errors;
            }

            var matching = ordered
                .Where(period => period.PeriodStart.Date >= rangeStart.Date && period.PeriodEnd.Date <= rangeEnd.Date)
                .ToList();

            if (!matching.Any())
            {
                errors.Add($"{label} range must match at least one generated rent period.");
                return errors;
            }

            if (matching.First().PeriodStart.Date != rangeStart.Date ||
                matching.Last().PeriodEnd.Date != rangeEnd.Date)
            {
                errors.Add($"{label} range must start and end on generated rent period boundaries.");
                return errors;
            }

            for (var index = 1; index < matching.Count; index++)
            {
                var previous = matching[index - 1];
                var current = matching[index];
                if (current.PeriodStart.Date != previous.PeriodEnd.Date.AddDays(1))
                {
                    errors.Add($"{label} periods must be continuous without skipped dates.");
                    break;
                }
            }

            return errors;
        }

        public static List<string> ValidateGeneratedSchedule(
            IReadOnlyCollection<RentPeriodSeedDto> periods,
            DateTimeOffset tenancyStart,
            DateTimeOffset? tenancyEnd,
            TenancyEndBehaviorEnum endBehavior,
            int paymentIntervalMonths = 1,
            DateTimeOffset? trackingStartDate = null)
        {
            var errors = new List<string>();
            var ordered = periods.OrderBy(period => period.PeriodStart).ToList();

            if (ordered.Count == 0)
            {
                errors.Add("At least one rent period must be generated before creating the tenancy.");
                return errors;
            }

            if (ordered.Any(period => period.Amount <= 0))
            {
                errors.Add("Every rent period must have an amount greater than 0.");
            }

            var expectedFirstStart = trackingStartDate?.Date ?? tenancyStart.Date;
            if (ordered.First().PeriodStart.Date != expectedFirstStart)
            {
                errors.Add("The first rent period must start on the configured rent-tracking boundary.");
            }

            if (endBehavior == TenancyEndBehaviorEnum.ExpireAutomatically)
            {
                if (!tenancyEnd.HasValue)
                {
                    errors.Add("An automatically expiring tenancy requires an end date.");
                }
                else if (ordered.Last().PeriodEnd.Date != tenancyEnd.Value.Date)
                {
                    errors.Add("The last rent period must end on the tenancy end date when the tenancy expires automatically.");
                }
            }

            for (var index = 0; index < ordered.Count; index++)
            {
                var period = ordered[index];
                var isLast = index == ordered.Count - 1;

                if (period.PeriodEnd.Date < period.PeriodStart.Date)
                {
                    errors.Add("Rent period end dates cannot be before their start dates.");
                    break;
                }

                var scheduleIndex = FindPeriodIndexContainingOrAfter(tenancyStart, period.PeriodStart.Date);
                if (MonthlyBoundary(tenancyStart, scheduleIndex).Date != period.PeriodStart.Date)
                {
                    errors.Add("Every rent period must start on a monthly anniversary of the tenancy.");
                    break;
                }

                var expectedNaturalEnd = MonthlyBoundary(tenancyStart, scheduleIndex + 1).AddDays(-1);
                var expectedEnd = isLast &&
                                  endBehavior == TenancyEndBehaviorEnum.ExpireAutomatically &&
                                  tenancyEnd.HasValue
                    ? tenancyEnd.Value.Date
                    : expectedNaturalEnd.Date;
                if (period.PeriodEnd.Date != expectedEnd)
                {
                    errors.Add("Every rent period must end the day before the next monthly tenancy anniversary, except a shortened final period.");
                    break;
                }

                var expectedGroupSequence = scheduleIndex / Math.Clamp(paymentIntervalMonths, 1, 12);
                if (period.BillingGroupSequence != expectedGroupSequence)
                {
                    errors.Add("Rent-period billing groups do not match the tenancy payment interval.");
                    break;
                }

                if (index == 0)
                {
                    continue;
                }

                var previous = ordered[index - 1];
                if (period.PeriodStart.Date != previous.PeriodEnd.Date.AddDays(1))
                {
                    errors.Add("Rent periods must be continuous without gaps or overlaps.");
                    break;
                }
            }

            return errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static RentPeriodStatusEnum ResolveUnpaidStatus(DateTimeOffset dueDate, DateTimeOffset nowUtc)
        {
            if (dueDate.Date > nowUtc.Date)
            {
                return RentPeriodStatusEnum.NotDueYet;
            }

            return dueDate.Date < nowUtc.Date
                ? RentPeriodStatusEnum.Overdue
                : RentPeriodStatusEnum.Due;
        }

        public static bool IsPaidStatus(RentPeriodStatusEnum status)
        {
            return status is RentPeriodStatusEnum.Paid
                or RentPeriodStatusEnum.PaidBeforeRentHub
                or RentPeriodStatusEnum.PaidInAdvance
                or RentPeriodStatusEnum.Waived
                or RentPeriodStatusEnum.Cancelled;
        }

        public static string StatusLabel(RentPeriodStatusEnum status)
        {
            return status switch
            {
                RentPeriodStatusEnum.NotDueYet => "Not due yet",
                RentPeriodStatusEnum.Due => "Due",
                RentPeriodStatusEnum.Overdue => "Overdue",
                RentPeriodStatusEnum.PendingPayment => "Pending payment",
                RentPeriodStatusEnum.Paid => "Paid",
                RentPeriodStatusEnum.PaidBeforeRentHub => "Historical payment",
                RentPeriodStatusEnum.PaidInAdvance => "Paid in advance",
                RentPeriodStatusEnum.Waived => "Waived",
                RentPeriodStatusEnum.Cancelled => "Cancelled",
                _ => "Unknown"
            };
        }

        private static DateTimeOffset ResolveGenerationEnd(
            DateTimeOffset startDate,
            DateTimeOffset? endDate,
            TenancyEndBehaviorEnum endBehavior,
            DateTimeOffset nowUtc,
            int paymentIntervalMonths,
            int firstPeriodIndex)
        {
            if (endBehavior == TenancyEndBehaviorEnum.ExpireAutomatically && endDate.HasValue)
            {
                return endDate.Value;
            }

            var currentPeriodIndex = FindPeriodIndexContainingOrAfter(startDate, nowUtc.Date);
            var interval = Math.Clamp(paymentIntervalMonths, 1, 12);
            var nextApplicableGroup = currentPeriodIndex / interval + 1;
            var firstTrackedGroup = firstPeriodIndex / interval;
            var requestedLastGroup = Math.Max(firstTrackedGroup, nextApplicableGroup);
            var selectedEnd = MonthlyBoundary(startDate, (requestedLastGroup + 1) * interval).AddDays(-1);

            if (endBehavior == TenancyEndBehaviorEnum.ContinueMonthToMonth &&
                endDate.HasValue &&
                endDate.Value.Date > selectedEnd.Date)
            {
                selectedEnd = endDate.Value;
            }

            return selectedEnd;
        }

        private static DateTimeOffset ResolveGroupDueDate(
            DateTimeOffset groupStart,
            DateTimeOffset groupEnd,
            int rentDueDay)
        {
            var daysInMonth = DateTime.DaysInMonth(groupStart.Year, groupStart.Month);
            var day = Math.Min(Math.Max(1, rentDueDay), daysInMonth);
            var result = new DateTimeOffset(groupStart.Year, groupStart.Month, day, 0, 0, 0, groupStart.Offset);
            if (result.Date < groupStart.Date)
            {
                var nextMonth = groupStart.AddMonths(1);
                day = Math.Min(Math.Max(1, rentDueDay), DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
                result = new DateTimeOffset(nextMonth.Year, nextMonth.Month, day, 0, 0, 0, groupStart.Offset);
            }

            return ClampDate(result, groupStart, groupEnd);
        }

        private static int FindPeriodIndexContainingOrAfter(DateTimeOffset tenancyStart, DateTime targetDate)
        {
            if (targetDate <= tenancyStart.Date)
            {
                return 0;
            }

            var index = Math.Max(
                0,
                (targetDate.Year - tenancyStart.Year) * 12 + targetDate.Month - tenancyStart.Month);
            while (index > 0 && MonthlyBoundary(tenancyStart, index).Date > targetDate)
            {
                index--;
            }

            while (MonthlyBoundary(tenancyStart, index + 1).Date <= targetDate)
            {
                index++;
            }

            return index;
        }

        private static DateTimeOffset AtDate(DateTime date, TimeSpan offset)
            => new(date.Year, date.Month, date.Day, 0, 0, 0, offset);

        private static DateTimeOffset ClampDate(DateTimeOffset value, DateTimeOffset min, DateTimeOffset max)
        {
            if (value.Date < min.Date)
            {
                return new DateTimeOffset(min.Date, min.Offset);
            }

            if (value.Date > max.Date)
            {
                return new DateTimeOffset(max.Date, max.Offset);
            }

            return value;
        }

        private static void MarkPaid(RentPeriodSeedDto period, RentPeriodStatusEnum status, DateTimeOffset? paidDate)
        {
            period.Status = status;
            period.PaidAmount = period.Amount;
            period.PaidDate = paidDate;
        }
    }
}
