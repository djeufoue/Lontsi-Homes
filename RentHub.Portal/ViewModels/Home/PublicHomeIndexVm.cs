using Common.CommunicationModels;
using RentHub.Portal.ViewModels.Auth;

namespace RentHub.Portal.ViewModels.Home
{
    public class PublicHomeIndexVm
    {
        public string? Search { get; set; }
        public string? City { get; set; }
        public string? Status { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
        public List<PublicApartmentCatalogItemDto> Apartments { get; set; } = new();
        public List<SubscriptionPlanOptionVm> Plans { get; set; } = new();
        public bool IsAuthenticated { get; set; }
        public bool IsLandlordOperator { get; set; }
        public bool IsVisitor { get; set; }
        public bool CanOpenMessages { get; set; }
    }
}
