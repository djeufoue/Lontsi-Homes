using System.ComponentModel.DataAnnotations;

namespace RentHub.API.Models.Entities
{
    public class SubscriptionInquiry
    {
        [Key]
        public int Id { get; set; }
        public string? RequesterUserId { get; set; }
        public ApplicationUser? RequesterUser { get; set; }
        [Required, MaxLength(160)] public string RequesterName { get; set; } = string.Empty;
        [Required, MaxLength(256)] public string RequesterEmail { get; set; } = string.Empty;
        [Required, MaxLength(80)] public string PlanName { get; set; } = string.Empty;
        public int PropertyCount { get; set; }
        public int ApartmentCount { get; set; }
        public int TenantCount { get; set; }
        public decimal ProposedMonthlyPrice { get; set; }
        public int CommitmentMonths { get; set; } = 6;
        [Required, MaxLength(96)] public string PublicAccessToken { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastMessageAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? RequesterLastReadAt { get; set; }
        public DateTimeOffset? AdminLastReadAt { get; set; }
        public ICollection<SubscriptionInquiryMessage> Messages { get; set; } = new List<SubscriptionInquiryMessage>();
    }
}
