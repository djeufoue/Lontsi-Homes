using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    public class UpdateApartmentReminderSettingsRequest
    {
        // Retained for backwards compatibility with older portal/API clients. New clients
        // should send RentReminderRules instead.
        [Range(0, 365)]
        public int RentReminderDaysBeforeDue { get; set; } = 10;

        [Range(0, 365)]
        public int LeaseTerminationReminderDaysBeforeEnd { get; set; } = 30;

        [Range(0, 20)]
        public int ManualRentReminderLimit { get; set; } = 2;

        [Range(0, 168)]
        public int ManualRentReminderCooldownHours { get; set; } = 24;

        public List<RentReminderRuleInputDto>? RentReminderRules { get; set; }
    }
}
