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
}
