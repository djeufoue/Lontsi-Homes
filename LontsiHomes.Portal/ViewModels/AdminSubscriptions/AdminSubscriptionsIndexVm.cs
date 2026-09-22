using Common.CommunicationModels;

namespace LontsiHomes.Portal.ViewModels.AdminSubscriptions
{
    public class AdminSubscriptionsIndexVm
    {
        public List<SubscriptionPlanDto> Plans { get; set; } = new();
        public List<PendingSubscriptionDto> Subscriptions { get; set; } = new();
        public List<SystemTransferAccountDto> TransferAccounts { get; set; } = new();
        public string Search { get; set; } = string.Empty;
    }
}
