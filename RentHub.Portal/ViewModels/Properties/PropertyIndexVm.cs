using Common.CommunicationModels;

namespace RentHub.Portal.ViewModels.Properties
{
    public class PropertyIndexVm
    {
        public string? Search { get; set; }
        public List<PropertyDto> Items { get; set; } = new();
    }
}
