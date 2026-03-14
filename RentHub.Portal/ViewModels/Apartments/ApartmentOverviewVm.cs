using Common.CommunicationModels;

namespace RentHub.Portal.ViewModels.Apartments
{
    public class ApartmentOverviewVm
    {
        public ApartmentDetailsDto Apartment { get; set; } = new();
        public List<TenancyDto> Tenancies { get; set; } = new();
        public List<ApartmentOwnerDto> Owners { get; set; } = new();
        public List<DocumentDto> Documents { get; set; } = new();

        public string? TenancySearch { get; set; }
        public string? MemberSearch { get; set; }
        public bool CanWrite { get; set; }
    }
}
