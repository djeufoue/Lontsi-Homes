using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    public class UpdateApartmentReminderSettingsRequest
    {
        [Range(0, 365)]
        public int RentReminderDaysBeforeDue { get; set; } = 10;

        [Range(0, 365)]
        public int LeaseTerminationReminderDaysBeforeEnd { get; set; } = 30;
    }
}
