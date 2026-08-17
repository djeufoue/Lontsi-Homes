using Common.CommunicationModels;
using RentHub.Portal.Helpers;

namespace RentHub.Portal.ViewModels.Properties
{
    public class PropertyOverviewVm
    {
        public PropertyDetailDto Property { get; set; } = new();
        public List<PropertyManagerDto> Managers { get; set; } = new();
        public List<PropertyManagerDto> FilteredManagers { get; set; } = new();
        public List<DocumentDto> Documents { get; set; } = new();

        public string? ApartmentSearch { get; set; }
        public string? MemberSearch { get; set; }
        public bool CanWrite { get; set; }
        public bool CanManageManagers { get; set; }
        public bool CanAddApartment { get; set; }
        public bool CanUploadDocuments { get; set; }
        public bool CanDeleteDocuments { get; set; }
        public bool CanManageMapVisibility { get; set; }

        public int UnitsPage { get; set; } = 1;
        public int UnitsPageSize { get; set; } = 6;
        public int TotalUnits { get; set; }
        public List<ApartmentDto> Units { get; set; } = new();

        public bool SuccessDialogShowCloseButton { get; set; } = true;
        public bool SuccessDialogAutoCloseEnabled { get; set; } = false;
        public int SuccessDialogAutoCloseSeconds { get; set; } = 5;
        public bool SuccessDialogShowSuccessMessages { get; set; } = true;
        public string SuccessDialogPosition { get; set; } = SuccessDialogHelper.DefaultPosition;
    }
}
