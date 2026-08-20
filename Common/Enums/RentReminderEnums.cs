namespace Common.Enums
{
    public enum RentReminderTimingEnum
    {
        BeforeDue = 0,
        OnDueDate = 1,
        AfterDue = 2
    }

    public enum RentReminderCategoryEnum
    {
        BeforeDue = 0,
        DueDate = 1,
        AfterDue = 2,
        Manual = 3
    }

    public enum RentReminderStatusEnum
    {
        Pending = 0,
        Sent = 1,
        PartiallySent = 2,
        Failed = 3,
        Cancelled = 4
    }

    public enum ReminderDeliveryStatusEnum
    {
        NotRequested = 0,
        Pending = 1,
        Sent = 2,
        Failed = 3,
        Skipped = 4
    }

    public enum RentReminderPeriodRelationEnum
    {
        Outstanding = 0,
        UpcomingInformation = 1
    }
}
