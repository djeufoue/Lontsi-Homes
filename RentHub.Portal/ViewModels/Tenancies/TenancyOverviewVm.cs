using Common.CommunicationModels;

namespace RentHub.Portal.ViewModels.Tenancies
{
    public class TenancyOverviewVm
    {
        public TenancyDetailsDto Tenancy { get; set; } = new();
        public List<TenancyMemberDto> Members { get; set; } = new();
        public List<DocumentDto> Documents { get; set; } = new();
        public List<RentPeriodDto> RentPeriods { get; set; } = new();
        public List<RentPeriodDto> AllRentPeriods { get; set; } = new();
        public RentSummaryDto RentSummary { get; set; } = new();
        public List<RentReminderHistoryDto> ReminderHistory { get; set; } = new();

        public string? MemberSearch { get; set; }
        public string? RentStatus { get; set; }
        public DateTime? RentFrom { get; set; }
        public DateTime? RentTo { get; set; }
        public string RentSortDirection { get; set; } = "priority";
        public int RentPage { get; set; } = 1;
        public int RentPageSize { get; set; } = 6;
        public int TotalRentPeriods { get; set; }
        public int TotalRentPages { get; set; } = 1;
        public int RentGroupPage { get; set; } = 1;
        public int RentGroupPageSize { get; set; } = 5;
        public int TotalRentGroups { get; set; }
        public int TotalRentGroupPages { get; set; } = 1;
        public List<string> RentStatuses { get; set; } = new();
        public bool IsRentPeriodsPage { get; set; }
    }

public class RentSummaryCardsVm
{
    public RentSummaryDto Summary { get; set; } = new();
    public DateTimeOffset? LeaseTerminationReminderDate { get; set; }
    public int PaymentIntervalMonths { get; set; } = 1;
}
}
