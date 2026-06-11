using Common.CommunicationModels;

namespace RentHub.Portal.ViewModels.Tenancies
{
    public class TenancyOverviewVm
    {
        public TenancyDetailsDto Tenancy { get; set; } = new();
        public List<TenancyMemberDto> Members { get; set; } = new();
        public List<DocumentDto> Documents { get; set; } = new();
        public List<RentPeriodDto> RentPeriods { get; set; } = new();

        public string? MemberSearch { get; set; }
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
