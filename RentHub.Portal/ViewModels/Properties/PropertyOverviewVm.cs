using Common.CommunicationModels;

namespace RentHub.Portal.ViewModels.Properties
{
    public class PropertyOverviewVm
    {
        public PropertyDetailDto Property { get; set; } = new();
        public List<PropertyManagerDto> Managers { get; set; } = new();
        public List<DocumentDto> Documents { get; set; } = new();

        public string? ApartmentSearch { get; set; }
        public bool CanWrite { get; set; }
    }
}
