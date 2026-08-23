using System.ComponentModel.DataAnnotations;

namespace RentHub.API.Models.Entities
{
    public class ConversationReadState
    {
        [Key]
        public int Id { get; set; }

        public int ConversationId { get; set; }
        public ApartmentConversation? Conversation { get; set; }

        public string UserId { get; set; } = string.Empty;
        public ApplicationUser? User { get; set; }

        public DateTimeOffset LastReadAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
