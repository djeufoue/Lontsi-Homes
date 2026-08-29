using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Common.Enums;

namespace Common.CommunicationModels
{
    public class TenancyOverviewDto
    {
        public TenancyDetailsDto Tenancy { get; set; } = new();

        public List<TenancyMemberDto> Members { get; set; } = new();

        public List<DocumentDto> Documents { get; set; } = new();

        public List<RentPeriodDto> RentPeriods { get; set; } = new();

        public RentSummaryDto RentSummary { get; set; } = new();

        public List<RentReminderHistoryDto> ReminderHistory { get; set; } = new();
    }

    public class TenancyDetailsDto
    {
        public int Id { get; set; }

        public int ApartmentId { get; set; }
        public string ApartmentName { get; set; } = string.Empty;

        public int PropertyId { get; set; }
        public string PropertyName { get; set; } = string.Empty;

        public DateTimeOffset StartDate { get; set; }
        public DateTimeOffset? EndDate { get; set; }
        public DateTimeOffset? LeaseTerminationReminderDate { get; set; }

        public decimal MonthlyRent { get; set; }
        public int MaxMembers { get; set; }
        public int RentDueDay { get; set; } = 1;
        public int PaymentIntervalMonths { get; set; } = 1;
        public TenancyEndBehaviorEnum EndBehavior { get; set; } = TenancyEndBehaviorEnum.NoEndDate;
        public DateTimeOffset RentTrackingStartDate { get; set; }
        public bool RentScheduleNeedsReview { get; set; }
        public bool CanCorrectRentSchedule { get; set; }
        public bool CanDeleteHistoricalRentPeriods { get; set; }
        public int DeletableHistoricalRentPeriodCount { get; set; }
        public DateTimeOffset? DeletableHistoricalFirstPeriodStart { get; set; }
        public DateTimeOffset? DeletableHistoricalLastPeriodEnd { get; set; }
        public DateTimeOffset? TerminatedAt { get; set; }
        public TenancyTerminationReasonEnum? TerminationReason { get; set; }
        public string? TerminationNotes { get; set; }
        public string Status { get; set; } = string.Empty;

        public bool CanWrite { get; set; }
        public bool CanEdit { get; set; }
        public bool CanDelete { get; set; }
        public bool CanTerminate { get; set; }
        public bool CanRenew { get; set; }
        public bool CanRequestTermination { get; set; }
        public bool CanRequestRenewal { get; set; }
        public bool CanViewMembers { get; set; }
        public bool CanAddMembers { get; set; }
        public bool CanEditMembers { get; set; }
        public bool CanEditMemberEmails { get; set; }
        public bool CanRemoveMembers { get; set; }
        public bool CanViewDocuments { get; set; }
        public bool CanUploadDocuments { get; set; }
        public bool CanDeleteDocuments { get; set; }
        public bool CanViewRent { get; set; }
        public bool CanViewRentReminderHistory { get; set; }
        public bool CanMarkRentPaid { get; set; }
        public bool CanCancelPendingPayment { get; set; }
        public bool CanSendRentReminder { get; set; }
    }
}
