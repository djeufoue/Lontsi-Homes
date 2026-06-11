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
}
