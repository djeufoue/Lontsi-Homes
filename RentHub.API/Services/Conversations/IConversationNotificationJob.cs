namespace RentHub.API.Services.Conversations
{
    public interface IConversationNotificationJob
    {
        Task SendMessageNotificationsAsync(int messageId);
        Task SendSubscriptionInquiryNotificationsAsync(int messageId);
    }
}
