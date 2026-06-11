namespace Common.Enums
{
    public enum RentPaymentImportModeEnum
    {
        AllGeneratedPeriodsUnpaid = 1,
        AllPastPeriodsPaidBeforeRentHub = 2,
        SomePeriodsWerePaid = 3,
        TenantPaidInAdvance = 4
    }
}
