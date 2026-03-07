using System.ComponentModel.DataAnnotations;

namespace RentHub.API.Models.Entities
{
    /// <summary>
    /// Stores configuration for rent reminders.  Each setting can be scoped to a specific landlord
    /// or property.  If PropertyId is null, the settings apply to all properties of the landlord.
    /// </summary>
    public class ReminderSettings
    {
        [Key]
        public int Id { get; set; }
        /// <summary>
        /// Optional property identifier that the settings apply to.  If null, the settings apply
        /// to all properties belonging to the landlord.
        /// </summary>
        public int? PropertyId { get; set; }
        public Property? Property { get; set; }
        /// <summary>
        /// Identifier of the landlord who owns these settings.
        /// </summary>
        public string LandlordId { get; set; } = string.Empty;
        public ApplicationUser? Landlord { get; set; }
        /// <summary>
        /// Number of days before rent due date to send reminders.  Example: 10 means send a reminder
        /// when 10 days remain before the due date.
        /// </summary>
        public int RentDueReminderDays { get; set; } = 10;
        /// <summary>
        /// Number of days after rent due date to send unpaid rent reminders.
        /// </summary>
        public int RentUnpaidReminderDays { get; set; } = 5;
        // Audit fields
        public bool IsDeleted { get; set; } = false;
        public string? CreatedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string? UpdatedBy { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public string? DeletedBy { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
    }
}