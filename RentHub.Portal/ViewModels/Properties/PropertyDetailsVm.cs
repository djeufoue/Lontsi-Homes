using Common.CommunicationModels;

namespace RentHub.Portal.ViewModels.Properties
{
    public class PropertyDetailsVm
    {
        public PropertyDetailDto Property { get; set; } = new PropertyDetailDto();
        public List<PropertyManagerDto> Managers { get; set; } = new();
        public List<DocumentDto> Documents { get; set; } = new(); // images = DocumentTypeEnum.Image, etc.

        public string? ApartmentSearch { get; set; }
    }
}
