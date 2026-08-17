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
        public bool CanViewFinancialInformation { get; set; }
        public bool CanEditFinancialInformation { get; set; }
        public bool CanManageMembers { get; set; }
        public bool CanManageDocuments { get; set; }
        public bool CanAddTenancy { get; set; }
        public bool CanEditTenancy { get; set; }
        public bool CanSendRentReminder { get; set; }
    }
}
