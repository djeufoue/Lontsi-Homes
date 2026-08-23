using System;
using System.Collections.Generic;
using System.Linq;
using Common.CommunicationModels;
using Common.Enums;

namespace Common.Helpers
{
    public static class RentPeriodScheduleHelper
    {
        public const int DefaultPreviewMonths = 1;

        public static List<RentPeriodSeedDto> GeneratePeriods(
            DateTimeOffset startDate,
            DateTimeOffset? endDate,
            TenancyEndBehaviorEnum endBehavior,
            decimal monthlyRent,
            int rentDueDay,
            DateTimeOffset nowUtc,
            int previewMonths = DefaultPreviewMonths)
        {
            var periods = new List<RentPeriodSeedDto>();
            if (monthlyRent <= 0 || previewMonths <= 0)
            {
                return periods;
            }

            var normalizedDueDay = Math.Clamp(rentDueDay, 1, 31);
            var normalizedStart = startDate.Date;
            var hardEnd = ResolveGenerationEnd(startDate, endDate, endBehavior, nowUtc, previewMonths).Date;
            var currentMonthStart = FirstDayOfMonth(startDate);

            while (currentMonthStart.Date <= hardEnd && periods.Count < 120)
            {
                var monthEnd = LastDayOfMonth(currentMonthStart);
                var periodStart = currentMonthStart.Date < normalizedStart
                    ? new DateTimeOffset(normalizedStart, startDate.Offset)
                    : currentMonthStart;
                var periodEnd = monthEnd.Date > hardEnd
                    ? new DateTimeOffset(hardEnd, startDate.Offset)
                    : monthEnd;

                if (periodEnd.Date < periodStart.Date)
                {
                    currentMonthStart = currentMonthStart.AddMonths(1);
                    continue;
                }

                var dueDate = ClampDate(BuildDueDate(currentMonthStart, normalizedDueDay), periodStart, periodEnd);
                periods.Add(new RentPeriodSeedDto
                {
                    PeriodStart = periodStart,
                    PeriodEnd = periodEnd,
                    DueDate = dueDate,
                    Amount = monthlyRent,
                    Status = ResolveUnpaidStatus(dueDate, nowUtc),
                    PaidAmount = 0
                });

                currentMonthStart = currentMonthStart.AddMonths(1);
            }

            return periods;
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
                    MarkPaid(period, RentPeriodStatusEnum.PaidBeforeRentHub, period.PeriodEnd);
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
                    MarkPaid(period, RentPeriodStatusEnum.PaidBeforeRentHub, period.PeriodEnd);
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
            TenancyEndBehaviorEnum endBehavior)
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

            if (ordered.First().PeriodStart.Date != tenancyStart.Date)
            {
                errors.Add("The first rent period must start on the tenancy start date.");
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
                var isFirst = index == 0;
                var isLast = index == ordered.Count - 1;
                var isPartialFinalExpiry = isLast && endBehavior == TenancyEndBehaviorEnum.ExpireAutomatically;

                if (period.PeriodEnd.Date < period.PeriodStart.Date)
                {
                    errors.Add("Rent period end dates cannot be before their start dates.");
                    break;
                }

                if (period.DueDate.Date < period.PeriodStart.Date || period.DueDate.Date > period.PeriodEnd.Date)
                {
                    errors.Add("Every rent due date must fall inside its rent period.");
                    break;
                }

                if (!isFirst && period.PeriodStart.Day != 1)
                {
                    errors.Add("After the first rent period, every period must start on the first day of the month.");
                    break;
                }

                if (!isPartialFinalExpiry && !IsLastDayOfMonth(period.PeriodEnd))
                {
                    errors.Add("Rent periods must end on the last day of the month, except the final period of an expiring tenancy.");
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
            int previewMonths)
        {
            if (endBehavior == TenancyEndBehaviorEnum.ExpireAutomatically && endDate.HasValue)
            {
                return endDate.Value;
            }

            var previewEnd = startDate.AddMonths(previewMonths).AddDays(-1);
            var operationalEnd = nowUtc.AddMonths(previewMonths).AddDays(-1);
            var selectedEnd = previewEnd > operationalEnd ? previewEnd : operationalEnd;

            if (endBehavior == TenancyEndBehaviorEnum.ContinueMonthToMonth &&
                endDate.HasValue &&
                endDate.Value.Date > selectedEnd.Date)
            {
                selectedEnd = endDate.Value;
            }

            return LastDayOfMonth(selectedEnd);
        }

        private static DateTimeOffset BuildDueDate(DateTimeOffset periodStart, int rentDueDay)
        {
            var daysInMonth = DateTime.DaysInMonth(periodStart.Year, periodStart.Month);
            var day = Math.Min(Math.Max(1, rentDueDay), daysInMonth);
            return new DateTimeOffset(periodStart.Year, periodStart.Month, day, 0, 0, 0, periodStart.Offset);
        }

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

        private static DateTimeOffset FirstDayOfMonth(DateTimeOffset value)
        {
            return new DateTimeOffset(value.Year, value.Month, 1, 0, 0, 0, value.Offset);
        }

        private static DateTimeOffset LastDayOfMonth(DateTimeOffset value)
        {
            var daysInMonth = DateTime.DaysInMonth(value.Year, value.Month);
            return new DateTimeOffset(value.Year, value.Month, daysInMonth, 0, 0, 0, value.Offset);
        }

        private static bool IsLastDayOfMonth(DateTimeOffset value)
        {
            return value.Day == DateTime.DaysInMonth(value.Year, value.Month);
        }

        private static void MarkPaid(RentPeriodSeedDto period, RentPeriodStatusEnum status, DateTimeOffset paidDate)
        {
            period.Status = status;
            period.PaidAmount = period.Amount;
            period.PaidDate = paidDate;
        }
    }
}
