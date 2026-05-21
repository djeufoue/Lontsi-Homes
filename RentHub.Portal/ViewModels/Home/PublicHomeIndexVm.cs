using RentHub.Portal.ViewModels.Auth;

namespace RentHub.Portal.ViewModels.Home
{
    public class PublicHomeIndexVm
    {
        public List<SubscriptionPlanOptionVm> Plans { get; set; } = new();
        public bool IsAuthenticated { get; set; }
        public bool IsLandlordOperator { get; set; }
    }
}
