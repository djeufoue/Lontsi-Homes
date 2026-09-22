using Common.Enums;
using LontsiHomes.API.Models.Entities;

namespace LontsiHomes.API.Helpers
{
    /// <summary>
    /// Derives an apartment's operational status from its tenancy timeline.
    /// The persisted Apartment.Status value is retained for backwards compatibility,
    /// but tenancy data is the source of truth for occupancy.
    /// </summary>
    public static class ApartmentStatusResolver
    {
        public static ApartmentStatusEnum Resolve(IEnumerable<Tenancy>? tenancies, DateTimeOffset nowUtc)
        {
            var tenancyList = tenancies?
                .Where(tenancy =>
                    !tenancy.IsDeleted &&
                    (!tenancy.TerminatedAt.HasValue || tenancy.TerminatedAt.Value.Date >= nowUtc.Date))
                .ToList() ?? new List<Tenancy>();

            if (tenancyList.Any(tenancy => IsCurrent(tenancy, nowUtc)))
            {
                return ApartmentStatusEnum.Occupied;
            }

            if (tenancyList.Any(tenancy => tenancy.StartDate.Date > nowUtc.Date))
            {
                return ApartmentStatusEnum.Reserved;
            }

            return ApartmentStatusEnum.Vacant;
        }

        private static bool IsCurrent(Tenancy tenancy, DateTimeOffset nowUtc)
        {
            if (tenancy.StartDate.Date > nowUtc.Date)
            {
                return false;
            }

            return !tenancy.EndDate.HasValue ||
                   tenancy.EndDate.Value.Date >= nowUtc.Date ||
                   tenancy.EndBehavior == TenancyEndBehaviorEnum.ContinueMonthToMonth;
        }
    }
}
