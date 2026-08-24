using Common.Enums;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    public class RentReminderRuleDto
    {
        public int Id { get; set; }
        public RentReminderTimingEnum Timing { get; set; }
        public int Days { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool EmailEnabled { get; set; } = true;
        public bool SmsEnabled { get; set; }
        public int SortOrder { get; set; }
    }

    public class RentReminderRuleInputDto
    {
        public int? Id { get; set; }
        public RentReminderTimingEnum Timing { get; set; }

        [Range(0, 365)]
        public int Days { get; set; }

        public bool IsEnabled { get; set; } = true;
        public bool EmailEnabled { get; set; } = true;
        public bool SmsEnabled { get; set; }
    }

    public class RentReminderPeriodSnapshotDto
    {
        public int RentPeriodId { get; set; }
        public DateTimeOffset PeriodStart { get; set; }
        public DateTimeOffset PeriodEnd { get; set; }
        public DateTimeOffset DueDate { get; set; }
        public decimal Amount { get; set; }
        public decimal PaidAmount { get; set; }
        public decimal OutstandingAmount { get; set; }
        public bool IsTrigger { get; set; }
        public bool IsUpcomingInformation { get; set; }
    }

    public class RentReminderHistoryDto
    {
        public int Id { get; set; }
        public int TenancyId { get; set; }
        public RentReminderCategoryEnum Category { get; set; }
        public string CategoryLabel { get; set; } = string.Empty;
        public bool IsManual { get; set; }
        public DateTimeOffset ScheduledFor { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? SentAt { get; set; }
        public RentReminderStatusEnum Status { get; set; }
        public string StatusLabel { get; set; } = string.Empty;
        public ReminderDeliveryStatusEnum EmailStatus { get; set; }
        public ReminderDeliveryStatusEnum SmsStatus { get; set; }
        public string RecipientEmail { get; set; } = string.Empty;
        public string RecipientPhone { get; set; } = string.Empty;
        public string RequestedByName { get; set; } = string.Empty;
        public string FailureReason { get; set; } = string.Empty;
        public DateTimeOffset? InvalidatedAt { get; set; }
        public string InvalidationReason { get; set; } = string.Empty;
        public decimal OutstandingAmount { get; set; }
        public int IncludedPeriodCount { get; set; }
        public List<RentReminderPeriodSnapshotDto> Periods { get; set; } = new();
    }

    public class RentSummaryDto
    {
        public decimal DueNowAmount { get; set; }
        public int DueNowPeriodCount { get; set; }
        public DateTimeOffset? OldestUnpaidDueDate { get; set; }
        public DateTimeOffset? LastPaidPeriodStart { get; set; }
        public DateTimeOffset? LastPaidPeriodEnd { get; set; }
        public DateTimeOffset? LastPaidAt { get; set; }
        public DateTimeOffset? NextPeriodStart { get; set; }
        public DateTimeOffset? NextPeriodEnd { get; set; }
        public DateTimeOffset? NextPeriodDueDate { get; set; }
        public decimal? NextPeriodAmount { get; set; }
        public DateTimeOffset? LastReminderSentAt { get; set; }
        public RentReminderCategoryEnum? LastReminderCategory { get; set; }
        public int ReminderCount { get; set; }
        public int ManualReminderCount { get; set; }
        public int ManualReminderLimit { get; set; }
        public bool CanSendManualReminder { get; set; }
        public string ManualReminderUnavailableReason { get; set; } = string.Empty;
    }

    public class SendManualRentReminderResultDto
    {
        public int ReminderId { get; set; }
        public RentReminderStatusEnum Status { get; set; }
        public int ManualReminderCount { get; set; }
        public int ManualReminderLimit { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
