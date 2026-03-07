using Common.CommunicationModels;

namespace RentHub.Portal.ViewModels.Apartments
{
    public class ApartmentIndexVm
    {
        public string? Search { get; set; }
        public List<ApartmentDto> Items { get; set; } = new();
    }
}
