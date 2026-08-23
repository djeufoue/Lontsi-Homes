using System.ComponentModel.DataAnnotations;

namespace RentHub.API.Models.Entities
{
    public class ApartmentConversation
    {
        [Key]
        public int Id { get; set; }

        public int ApartmentId { get; set; }
        public Apartment? Apartment { get; set; }

        public string LandlordId { get; set; } = string.Empty;
        public ApplicationUser? Landlord { get; set; }

        public string VisitorId { get; set; } = string.Empty;
        public ApplicationUser? Visitor { get; set; }

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastMessageAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastVisitorMessageAt { get; set; }
        public DateTimeOffset? LastLandlordMessageAt { get; set; }
        public DateTimeOffset? VisitorLastReadAt { get; set; }
        public DateTimeOffset? LandlordLastReadAt { get; set; }
        public bool IsPropertyTeamConversation { get; set; }

        public ICollection<ConversationMessage> Messages { get; set; } = new List<ConversationMessage>();
        public ICollection<ConversationReadState> ReadStates { get; set; } = new List<ConversationReadState>();
    }
}
