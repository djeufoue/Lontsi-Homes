using Common.Enums;
using System.ComponentModel.DataAnnotations;

namespace RentHub.API.Models.Entities
{
    public class RentReminderTrigger
    {
        [Key]
        public int Id { get; set; }

        public int RentReminderId { get; set; }
        public RentReminder? RentReminder { get; set; }

        public int RentPeriodId { get; set; }
        public RentPeriod? RentPeriod { get; set; }

        public int? ApartmentRentReminderRuleId { get; set; }
        public ApartmentRentReminderRule? ApartmentRentReminderRule { get; set; }

        public RentReminderCategoryEnum Category { get; set; }
        public int ManualSequence { get; set; }

        [Required, MaxLength(160)]
        public string TriggerKey { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
