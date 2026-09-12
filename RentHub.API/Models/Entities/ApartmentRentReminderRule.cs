using Common.Enums;
using System.ComponentModel.DataAnnotations;

namespace RentHub.API.Models.Entities
{
    public class ApartmentRentReminderRule
    {
        [Key]
        public int Id { get; set; }

        public int ApartmentId { get; set; }
        public Apartment? Apartment { get; set; }

        public RentReminderTimingEnum Timing { get; set; }
        public int Days { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool EmailEnabled { get; set; } = true;
        public bool SmsEnabled { get; set; }
        public bool WhatsAppEnabled { get; set; }
        public int SortOrder { get; set; }

        public bool IsDeleted { get; set; }
        public string? CreatedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string? UpdatedBy { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public string? DeletedBy { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
    }
}
