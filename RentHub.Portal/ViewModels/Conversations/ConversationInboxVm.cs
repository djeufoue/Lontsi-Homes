using Common.CommunicationModels;

namespace RentHub.Portal.ViewModels.Conversations
{
    public class ConversationInboxVm
    {
        public List<ConversationListItemDto> Items { get; set; } = new();
        public ConversationThreadDto? SelectedConversation { get; set; }
        public int? SelectedConversationId { get; set; }
        public bool IsVisitor { get; set; }
        public bool IsTenant { get; set; }
        public bool IsLandlord { get; set; }
        public bool IsManager { get; set; }
        public bool IsAdmin { get; set; }
        public int? SelectedPropertyId { get; set; }
        public int? SelectedApartmentId { get; set; }
        public ConversationWorkspaceDto Workspace { get; set; } = new();
        public string SelectedKind { get; set; } = "apartment";
        public List<SubscriptionInquiryListItemDto> SubscriptionItems { get; set; } = new();
        public SubscriptionInquiryThreadDto? SelectedSubscriptionInquiry { get; set; }
    }
}
