using System;
using Common.Enums;

namespace Common.CommunicationModels
{
    /// <summary>
    /// Data transfer object representing a tenancy listing or detail.
    /// Includes references to related apartment and property names.
    /// </summary>
    public class TenancyDto
    {
        public int Id { get; set; }
        public string ApartmentName { get; set; } = string.Empty;
        public string PropertyName { get; set; } = string.Empty;
        public DateTimeOffset StartDate { get; set; }
        public DateTimeOffset? EndDate { get; set; }
        public decimal MonthlyRent { get; set; }
        public int MaxMembers { get; set; }
        public int RentDueDay { get; set; } = 1;
        public int PaymentIntervalMonths { get; set; } = 1;
        public TenancyEndBehaviorEnum EndBehavior { get; set; } = TenancyEndBehaviorEnum.NoEndDate;
        public int FutureRentPeriodCount { get; set; } = 1;
        public DateTimeOffset RentTrackingStartDate { get; set; }
        public bool RentScheduleNeedsReview { get; set; }
        public DateTimeOffset? TerminatedAt { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset? PaidThroughDate { get; set; }
        public DateTimeOffset? NextRentDueDate { get; set; }
        public DateTimeOffset? NextRentReminderDate { get; set; }
        public DateTimeOffset? LeaseTerminationReminderDate { get; set; }
        public bool IsPaidInAdvance { get; set; }
        public DateTimeOffset? LastPaidPeriodStart { get; set; }
        public DateTimeOffset? LastPaidPeriodEnd { get; set; }
        public DateTimeOffset? LastPaidAt { get; set; }
        public decimal DueNowAmount { get; set; }
        public int DueNowPeriodCount { get; set; }
        public DateTimeOffset? OldestUnpaidDueDate { get; set; }
        public DateTimeOffset? NextUpcomingPeriodStart { get; set; }
        public DateTimeOffset? NextUpcomingPeriodEnd { get; set; }
        public decimal? NextUpcomingAmount { get; set; }
        public DateTimeOffset? LastReminderSentAt { get; set; }
        public RentReminderCategoryEnum? LastReminderCategory { get; set; }
        public int ReminderCount { get; set; }
        /// <summary>
        /// Indicates whether the current authenticated user is the landlord/owner of the apartment.
        /// </summary>
        public bool IsOwner { get; set; }
    }
}
