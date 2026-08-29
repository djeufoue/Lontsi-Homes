using Common.Helpers;
using RentHub.API.Models.Entities;

namespace RentHub.API.Helpers
{
    public static class RentPaymentGroupHelper
    {
        public static List<RentPeriod> SelectOldestOutstandingGroup(IEnumerable<RentPeriod> periods)
        {
            var openPeriods = periods
                .Where(period =>
                    !period.IsDeleted &&
                    !RentPeriodScheduleHelper.IsPaidStatus(period.Status))
                .OrderBy(period => period.PeriodStart)
                .ToList();

            var firstOpenPeriod = openPeriods.FirstOrDefault();
            return firstOpenPeriod == null
                ? new List<RentPeriod>()
                : openPeriods
                    .Where(period => period.BillingGroupSequence == firstOpenPeriod.BillingGroupSequence)
                    .ToList();
        }
    }
}
