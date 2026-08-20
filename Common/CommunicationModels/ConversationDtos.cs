using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    public class ConversationListItemDto
    {
        public int ConversationId { get; set; }
        public int ApartmentId { get; set; }
        public string ApartmentName { get; set; } = string.Empty;
        public string PropertyName { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string CounterpartyName { get; set; } = string.Empty;
        public string LastMessagePreview { get; set; } = string.Empty;
        public DateTimeOffset LastMessageAt { get; set; }
        public bool HasUnreadMessages { get; set; }
        public string? LeadImageUrl { get; set; }
    }

    public class ConversationThreadDto
    {
        public int ConversationId { get; set; }
        public int ApartmentId { get; set; }
        public string ApartmentName { get; set; } = string.Empty;
        public string PropertyName { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public string LandlordName { get; set; } = string.Empty;
        public string VisitorName { get; set; } = string.Empty;
        public string CounterpartyName { get; set; } = string.Empty;
        public string? LeadImageUrl { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastMessageAt { get; set; }
        public List<ConversationMessageDto> Messages { get; set; } = new();
    }

    public class ConversationMessageDto
    {
        public int MessageId { get; set; }
        public string SenderId { get; set; } = string.Empty;
        public string SenderName { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public DateTimeOffset SentAt { get; set; }
        public bool SentByCurrentUser { get; set; }
        public bool IsRead { get; set; }
    }

    public class StartConversationRequest
    {
        [Required]
        [StringLength(1500, MinimumLength = 2)]
        public string InitialMessage { get; set; } = string.Empty;
    }

    public class CreateConversationMessageRequest
    {
        [Required]
        [StringLength(1500, MinimumLength = 1)]
        public string Message { get; set; } = string.Empty;
    }

    public class CreateSubscriptionInquiryRequest
    {
        [Required, StringLength(80)]
        public string PlanName { get; set; } = string.Empty;

        [Range(1, 100000)]
        public int PropertyCount { get; set; }

        [Range(1, 1000000)]
        public int ApartmentCount { get; set; }

        [Range(1, 1000000)]
        public int TenantCount { get; set; }

        [Range(
            typeof(decimal),
            "0.01",
            "1000000000",
            ParseLimitsInInvariantCulture = true,
            ConvertValueInInvariantCulture = true)]
        public decimal ProposedMonthlyPrice { get; set; }

        [Range(6, 1200)]
        public int CommitmentMonths { get; set; }

        [StringLength(160)]
        public string? RequesterName { get; set; }

        [EmailAddress, StringLength(256)]
        public string? RequesterEmail { get; set; }

        [Required, StringLength(3000, MinimumLength = 2)]
        public string Message { get; set; } = string.Empty;
    }

    public class CreateSubscriptionInquiryMessageRequest
    {
        [Required, StringLength(3000, MinimumLength = 1)]
        public string Message { get; set; } = string.Empty;
    }

    public class SubscriptionInquiryCreatedDto
    {
        public int InquiryId { get; set; }
        public string PublicAccessToken { get; set; } = string.Empty;
    }

    public class SubscriptionInquiryListItemDto
    {
        public int InquiryId { get; set; }
        public string PlanName { get; set; } = string.Empty;
        public string RequesterName { get; set; } = string.Empty;
        public string RequesterEmail { get; set; } = string.Empty;
        public decimal ProposedMonthlyPrice { get; set; }
        public int CommitmentMonths { get; set; }
        public string LastMessagePreview { get; set; } = string.Empty;
        public DateTimeOffset LastMessageAt { get; set; }
        public bool HasUnreadMessages { get; set; }
    }

    public class SubscriptionInquiryMessageDto
    {
        public int MessageId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public DateTimeOffset SentAt { get; set; }
        public bool SentByCurrentUser { get; set; }
    }

    public class SubscriptionInquiryThreadDto
    {
        public int InquiryId { get; set; }
        public string PublicAccessToken { get; set; } = string.Empty;
        public string PlanName { get; set; } = string.Empty;
        public string RequesterName { get; set; } = string.Empty;
        public string RequesterEmail { get; set; } = string.Empty;
        public int PropertyCount { get; set; }
        public int ApartmentCount { get; set; }
        public int TenantCount { get; set; }
        public decimal ProposedMonthlyPrice { get; set; }
        public int CommitmentMonths { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastMessageAt { get; set; }
        public List<SubscriptionInquiryMessageDto> Messages { get; set; } = new();
    }
}
