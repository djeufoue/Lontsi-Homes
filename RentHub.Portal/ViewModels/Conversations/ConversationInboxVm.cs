using Common.CommunicationModels;

namespace RentHub.Portal.ViewModels.Conversations
{
    public class ConversationInboxVm
    {
        public List<ConversationListItemDto> Items { get; set; } = new();
        public ConversationThreadDto? SelectedConversation { get; set; }
        public int? SelectedConversationId { get; set; }
        public bool IsVisitor { get; set; }
        public bool IsLandlord { get; set; }
    }
}
