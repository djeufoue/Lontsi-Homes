using System;

namespace Common.Helpers
{
    /// <summary>
    /// Conservative product guardrails based on the Cameroonian tenancy-notice
    /// guidance supplied for this application. This is not a legal determination;
    /// the signed tenancy and applicable local rules still control.
    /// </summary>
    public static class CameroonTenancyNoticePolicy
    {
        public const int StandardLandlordNoticeMonths = 3;

        public static DateTime MinimumLandlordTerminationDate(DateTime today)
            => today.Date.AddMonths(StandardLandlordNoticeMonths);

        public static DateTimeOffset MinimumLandlordTerminationDate(DateTimeOffset today)
            => new DateTimeOffset(
                today.Year,
                today.Month,
                today.Day,
                0,
                0,
                0,
                TimeSpan.Zero).AddMonths(StandardLandlordNoticeMonths);
    }
}
