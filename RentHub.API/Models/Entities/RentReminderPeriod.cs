using Common.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RentHub.API.Models.Entities
{
    public class RentReminderPeriod
    {
        [Key]
        public int Id { get; set; }

        public int RentReminderId { get; set; }
        public RentReminder? RentReminder { get; set; }

        public int RentPeriodId { get; set; }
        public RentPeriod? RentPeriod { get; set; }

        public RentReminderPeriodRelationEnum Relation { get; set; }
        public bool IsTrigger { get; set; }
        public DateTimeOffset PeriodStartSnapshot { get; set; }
        public DateTimeOffset PeriodEndSnapshot { get; set; }
        public DateTimeOffset DueDateSnapshot { get; set; }

        [Column(TypeName = "decimal(14,2)")]
        public decimal AmountSnapshot { get; set; }

        [Column(TypeName = "decimal(14,2)")]
        public decimal PaidAmountSnapshot { get; set; }

        [Column(TypeName = "decimal(14,2)")]
        public decimal OutstandingAmountSnapshot { get; set; }
    }
}
