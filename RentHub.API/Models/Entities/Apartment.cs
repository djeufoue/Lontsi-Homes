using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Common.Enums;

namespace RentHub.API.Models.Entities
{
    /// <summary>
    /// Represents an individual apartment unit that can be advertised and rented.  Each
    /// apartment belongs to a property and has an associated landlord.
    /// </summary>
    public class Apartment
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string Name { get; set; } = string.Empty;

        // The user who added the apartment (could be landlord or delegated manager)
        public string AdderId { get; set; } = string.Empty;
        public ApplicationUser? Adder { get; set; }

        // Foreign key to the containing property
        public int PropertyId { get; set; }
        public Property? Property { get; set; }

        public int NumberOfRooms { get; set; }
        public int NumberOfBathrooms { get; set; }
        public int Area { get; set; } // Surface area in square metres

        public int? FloorNumber { get; set; }

        [Column(TypeName = "decimal(14,2)")]
        public decimal Price { get; set; }

        [Column(TypeName = "decimal(14,2)")]
        public decimal DepositPrice { get; set; }

        /// <summary>
        /// Number of days before the next rent due date to notify the main tenant.
        /// </summary>
        public int RentReminderDaysBeforeDue { get; set; } = 10;

        /// <summary>
        /// Maximum number of extra reminders that an authorized user can send manually
        /// for the current oldest unpaid rent period. Automatic rules are not counted.
        /// </summary>
        public int ManualRentReminderLimit { get; set; } = 2;

        /// <summary>
        /// Minimum delay between two manual reminders for the same rent period.
        /// </summary>
        public int ManualRentReminderCooldownHours { get; set; } = 24;

        /// <summary>
        /// Number of days before tenancy end date to notify the tenant about lease termination.
        /// </summary>
        public int LeaseTerminationReminderDaysBeforeEnd { get; set; } = 30;

        public ApartmentStatusEnum Status { get; set; } = ApartmentStatusEnum.Vacant;

        public ApartmentTypeEnum Type { get; set; } = ApartmentTypeEnum.Studio;

        // Audit fields
        /// <summary>
        /// Indicates whether the apartment has been soft deleted.  Deleted apartments are excluded
        /// from queries via a global filter.
        /// </summary>
        public bool IsDeleted { get; set; } = false;

        /// <summary>
        /// Identifier of the user who created this record.
        /// </summary>
        public string? CreatedBy { get; set; }

        /// <summary>
        /// Timestamp when the record was created (UTC).
        /// </summary>
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Identifier of the user who last modified this record.
        /// </summary>
        public string? UpdatedBy { get; set; }

        /// <summary>
        /// Timestamp when the record was last modified (UTC).
        /// </summary>
        public DateTimeOffset? UpdatedAt { get; set; }

        /// <summary>
        /// Identifier of the user who deleted this record.
        /// </summary>
        public string? DeletedBy { get; set; }

        /// <summary>
        /// Timestamp when the record was deleted (UTC).  Only set when IsDeleted is true.
        /// </summary>
        public DateTimeOffset? DeletedAt { get; set; }

        // Navigational property for tenancies associated with this apartment
        public ICollection<Tenancy> Tenancies { get; set; } = new List<Tenancy>();
        public ICollection<ApartmentRentReminderRule> RentReminderRules { get; set; } = new List<ApartmentRentReminderRule>();
    }
}
