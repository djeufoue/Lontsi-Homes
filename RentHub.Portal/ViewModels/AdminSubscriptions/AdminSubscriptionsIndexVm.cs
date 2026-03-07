using Common.CommunicationModels;

namespace RentHub.Portal.ViewModels.AdminSubscriptions
{
    public class AdminSubscriptionsIndexVm
    {
        public List<SubscriptionPlanDto> Plans { get; set; } = new();
        public List<PendingSubscriptionDto> PendingSubscriptions { get; set; } = new();
    }
}
