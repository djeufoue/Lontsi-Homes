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

        public int UnitsPage { get; set; } = 1;
        public int UnitsPageSize { get; set; } = 6;
        public int TotalUnits { get; set; }
        public List<ApartmentDto> Units { get; set; } = new();

        public bool SuccessDialogShowCloseButton { get; set; } = true;
        public int SuccessDialogAutoCloseSeconds { get; set; } = 5;
    }
}
