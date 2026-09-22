using Common.CommunicationModels;

namespace LontsiHomes.Portal.ViewModels.Home
{
    public class PublicApartmentPageVm
    {
        public PublicApartmentOverviewDto Apartment { get; set; } = new();
        public ConversationThreadDto? Conversation { get; set; }
        public bool IsAuthenticated { get; set; }
        public bool IsVisitor { get; set; }
        public bool IsLandlord { get; set; }
        public bool CanStartConversation { get; set; }
        public string LoginReturnUrl { get; set; } = string.Empty;
        public string RegisterVisitorReturnUrl { get; set; } = string.Empty;
    }
}
