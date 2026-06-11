using System;
using System.Collections.Generic;
using System.Linq;
using Common.CommunicationModels;
using Common.Enums;

namespace Common.Helpers
{
    public static class RentPeriodScheduleHelper
    {
        public const int DefaultPreviewMonths = 12;

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
            var hardEnd = ResolveGenerationEnd(startDate, endDate, endBehavior, nowUtc, previewMonths);
            var currentStart = startDate.Date;

            while (currentStart <= hardEnd.Date && periods.Count < 120)
            {
                var nextStart = currentStart.AddMonths(1);
                var periodEnd = nextStart.AddDays(-1);
                if (endBehavior == TenancyEndBehaviorEnum.ExpireAutomatically && endDate.HasValue && periodEnd > endDate.Value.Date)
                {
                    periodEnd = endDate.Value.Date;
                }

                var dueDate = BuildDueDate(currentStart, normalizedDueDay);
                periods.Add(new RentPeriodSeedDto
                {
                    PeriodStart = currentStart,
                    PeriodEnd = periodEnd,
                    DueDate = dueDate,
                    Amount = monthlyRent,
                    Status = ResolveUnpaidStatus(dueDate, nowUtc),
                    PaidAmount = 0
                });

                currentStart = nextStart;
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
                             period.PeriodEnd.Date <= paidInAdvanceTo.Value.Date))
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

            var expected = matching.First().PeriodStart.Date;
            foreach (var period in matching)
            {
                if (period.PeriodStart.Date != expected)
                {
                    errors.Add($"{label} periods must be continuous without skipping a month.");
                    break;
                }

                expected = period.PeriodStart.Date.AddMonths(1);
            }

            return errors;
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
                RentPeriodStatusEnum.PaidBeforeRentHub => "Paid before RentHub",
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
            return previewEnd > operationalEnd ? previewEnd : operationalEnd;
        }

        private static DateTimeOffset BuildDueDate(DateTimeOffset periodStart, int rentDueDay)
        {
            var daysInMonth = DateTime.DaysInMonth(periodStart.Year, periodStart.Month);
            var day = Math.Min(Math.Max(1, rentDueDay), daysInMonth);
            return new DateTimeOffset(periodStart.Year, periodStart.Month, day, 0, 0, 0, periodStart.Offset);
        }

        private static void MarkPaid(RentPeriodSeedDto period, RentPeriodStatusEnum status, DateTimeOffset paidDate)
        {
            period.Status = status;
            period.PaidAmount = period.Amount;
            period.PaidDate = paidDate;
        }
    }
}
