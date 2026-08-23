using System.ComponentModel.DataAnnotations;

namespace RentHub.API.Models.Entities
{
    public class ConversationMessage
    {
        [Key]
        public int Id { get; set; }

        public int ConversationId { get; set; }
        public ApartmentConversation? Conversation { get; set; }

        public string SenderId { get; set; } = string.Empty;
        public ApplicationUser? Sender { get; set; }

        public int? ReplyToMessageId { get; set; }
        public ConversationMessage? ReplyToMessage { get; set; }

        [Required]
        [MaxLength(1500)]
        public string Body { get; set; } = string.Empty;

        public bool IsPropertyBroadcast { get; set; }

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
