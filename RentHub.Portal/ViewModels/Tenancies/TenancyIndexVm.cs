using Common.CommunicationModels;

namespace RentHub.Portal.ViewModels.Tenancies
{
    public class TenancyIndexVm
    {
        public string? Search { get; set; }
        public List<TenancyDto> Items { get; set; } = new();
    }
}
