using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace RentHub.API.Models.Entities
{
    /// <summary>
    /// Represents a rental agreement between a tenant and a landlord for a specific
    /// apartment.  Multiple tenants can be associated via TenancyMember if needed.
    /// </summary>
    public class Tenancy
    {
        [Key]
        public int Id { get; set; }

        public int ApartmentId { get; set; }
        public Apartment? Apartment { get; set; }

        public DateTimeOffset StartDate { get; set; }
        public DateTimeOffset? EndDate { get; set; }
        public DateTimeOffset? RenewalReminderSentAt { get; set; }
        public DateTimeOffset? RenewalReminderSentForEndDate { get; set; }

        [Range(0, double.MaxValue)]
        public decimal MonthlyRent { get; set; }

        /// <summary>
        /// Maximum number of members allowed to be associated with this tenancy.
        /// Landlords or authorized managers/owners can modify this value.
        /// </summary>
        public int MaxMembers { get; set; } = 5;

        [Range(1, 31)]
        public int RentDueDay { get; set; } = 1;

        [Range(1, 12)]
        public int PaymentIntervalMonths { get; set; } = 1;

        public TenancyEndBehaviorEnum EndBehavior { get; set; } = TenancyEndBehaviorEnum.NoEndDate;

        // Transitional deployment-only column. Rent-period provisioning is now
        // derived exclusively from PaymentIntervalMonths. Remove this property
        // with the follow-up database migration after production validation.
        [Range(1, 12)]
        public int FutureRentPeriodCount { get; set; } = 1;

        /// <summary>
        /// First monthly coverage period managed by Lontsi Homes.  It may be later
        /// than StartDate when an existing, fully-paid tenancy is onboarded.
        /// </summary>
        public DateTimeOffset RentTrackingStartDate { get; set; }

        /// <summary>
        /// Legacy calendar-month schedules are paused until their owner confirms
        /// the contractual anchor, payment interval, and first tracked period.
        /// </summary>
        public bool RentScheduleNeedsReview { get; set; }

        public DateTimeOffset? TerminatedAt { get; set; }
        public TenancyTerminationReasonEnum? TerminationReason { get; set; }
        public string? TerminationNotes { get; set; }
        public string? TerminatedBy { get; set; }

        // Audit fields
        public bool IsDeleted { get; set; } = false;
        public string? CreatedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string? UpdatedBy { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public string? DeletedBy { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }

        // Navigation property for members associated with this tenancy
        public ICollection<TenancyMember> Members { get; set; } = new List<TenancyMember>();
        public ICollection<RentPeriod> RentPeriods { get; set; } = new List<RentPeriod>();
        public ICollection<TenancyExtensionRequest> ExtensionRequests { get; set; } = new List<TenancyExtensionRequest>();
        public ICollection<TenancyTerminationRequest> TerminationRequests { get; set; } = new List<TenancyTerminationRequest>();
        public ICollection<RentReminder> RentReminders { get; set; } = new List<RentReminder>();
    }
}

