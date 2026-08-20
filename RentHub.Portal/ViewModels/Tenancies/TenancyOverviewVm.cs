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
        public string RentSortDirection { get; set; } = "asc";
        public int RentPage { get; set; } = 1;
        public int RentPageSize { get; set; } = 6;
        public int TotalRentPeriods { get; set; }
        public int TotalRentPages { get; set; } = 1;
        public List<string> RentStatuses { get; set; } = new();
        public bool IsRentPeriodsPage { get; set; }
    }

    public class TenancyMemberProfileVm
    {
        public TenancyDetailsDto Tenancy { get; set; } = new();
        public TenancyMemberDto Member { get; set; } = new();
        public List<RentPeriodDto> RentPeriods { get; set; } = new();

        public decimal OutstandingBalance { get; set; }
        public int PaidPeriods { get; set; }
        public int TotalPeriods { get; set; }
        public int ProgressPercent { get; set; }
        public RentPeriodDto? NextPayablePeriod { get; set; }
    }
}
