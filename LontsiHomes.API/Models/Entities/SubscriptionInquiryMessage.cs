using System.ComponentModel.DataAnnotations;

namespace LontsiHomes.API.Models.Entities
{
    public class SubscriptionInquiryMessage
    {
        [Key]
        public int Id { get; set; }
        public int SubscriptionInquiryId { get; set; }
        public SubscriptionInquiry? SubscriptionInquiry { get; set; }
        public string? SenderUserId { get; set; }
        public ApplicationUser? SenderUser { get; set; }
        [Required, MaxLength(160)] public string SenderName { get; set; } = string.Empty;
        [Required, MaxLength(256)] public string SenderEmail { get; set; } = string.Empty;
        public bool SentByAdmin { get; set; }
        [Required, MaxLength(3000)] public string Body { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
