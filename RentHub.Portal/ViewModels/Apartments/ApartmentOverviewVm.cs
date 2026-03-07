using Common.CommunicationModels;

namespace RentHub.Portal.ViewModels.Apartments
{
    public class ApartmentOverviewVm
    {
        public int ApartmentId { get; set; }
        public string ApartmentName { get; set; } = "";
        public string PropertyName { get; set; } = "";

        public List<TenancyDto> Tenancies { get; set; } = new();
        public List<ApartmentOwnerDto> Owners { get; set; } = new();
        public List<DocumentDto> Documents { get; set; } = new();

        public string? TenancySearch { get; set; }
    }
}
