using Common.CommunicationModels;
using Common.Enums;
using RentHub.API.Models.Entities;

namespace RentHub.API.Helpers
{
    public static class TenancyReminderHelpers
    {
        public static TenancyReminderSnapshot BuildSnapshot(
            Tenancy tenancy,
            Apartment apartment,
            IEnumerable<Payment> payments,
            int rentReminderDaysBeforeDue,
            int leaseTerminationReminderDaysBeforeEnd,
            DateTimeOffset nowUtc)
        {
            if (tenancy.MonthlyRent <= 0)
            {
                return new TenancyReminderSnapshot
                {
                    LeaseTerminationReminderDate = tenancy.EndDate?.AddDays(-Math.Max(0, leaseTerminationReminderDaysBeforeEnd))
                };
            }

            var successfulPayments = payments
                .Where(payment => !payment.IsDeleted && payment.Status == PaymentStatusEnum.Success)
                .ToList();

            var totalPaid = successfulPayments.Sum(payment => payment.Amount);
            var periodsCovered = totalPaid <= 0
                ? 0
                : (int)Math.Floor(totalPaid / tenancy.MonthlyRent);

            var nextRentDueDate = tenancy.StartDate.AddMonths(Math.Max(0, periodsCovered));
            DateTimeOffset? paidThroughDate = periodsCovered > 0 ? nextRentDueDate.AddDays(-1) : null;
            var duePeriodsByNow = CountDuePeriodsByDate(tenancy.StartDate, nowUtc);

            return new TenancyReminderSnapshot
            {
                PaidThroughDate = paidThroughDate,
                NextRentDueDate = nextRentDueDate,
                NextRentReminderDate = nextRentDueDate.AddDays(-Math.Max(0, rentReminderDaysBeforeDue)),
                LeaseTerminationReminderDate = tenancy.EndDate?.AddDays(-Math.Max(0, leaseTerminationReminderDaysBeforeEnd)),
                IsPaidInAdvance = periodsCovered > duePeriodsByNow
            };
        }

        public static int CountDuePeriodsByDate(DateTimeOffset startDate, DateTimeOffset asOf)
        {
            if (asOf < startDate)
            {
                return 0;
            }

            var dueDate = startDate;
            var count = 0;
            while (dueDate <= asOf)
            {
                count++;
                dueDate = dueDate.AddMonths(1);
            }

            return count;
        }

        public sealed class TenancyReminderSnapshot
        {
            public DateTimeOffset? PaidThroughDate { get; init; }
            public DateTimeOffset? NextRentDueDate { get; init; }
            public DateTimeOffset? NextRentReminderDate { get; init; }
            public DateTimeOffset? LeaseTerminationReminderDate { get; init; }
            public bool IsPaidInAdvance { get; init; }

            public TenancyDto ApplyTo(TenancyDto dto)
            {
                dto.PaidThroughDate = PaidThroughDate;
                dto.NextRentDueDate = NextRentDueDate;
                dto.NextRentReminderDate = NextRentReminderDate;
                dto.LeaseTerminationReminderDate = LeaseTerminationReminderDate;
                dto.IsPaidInAdvance = IsPaidInAdvance;
                return dto;
            }
        }
    }
}
