namespace Common.Enums
{
    public enum RentPeriodStatusEnum
    {
        NotDueYet = 0,
        Due = 1,
        Overdue = 2,
        PendingPayment = 3,
        Paid = 4,
        PaidBeforeRentHub = 5,
        PaidInAdvance = 6,
        Waived = 7,
        Cancelled = 8
    }
}
