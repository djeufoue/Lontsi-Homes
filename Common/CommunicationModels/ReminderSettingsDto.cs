namespace Common.CommunicationModels
{
    /// <summary>
    /// Data transfer object used for creating or updating reminder settings.  It
    /// specifies the number of days before and after rent due dates when reminders
    /// should be sent.
    /// </summary>
    public class ReminderSettingsDto
    {
        public int? PropertyId { get; set; }
        public int RentDueReminderDays { get; set; }
        public int RentUnpaidReminderDays { get; set; }
    }
}