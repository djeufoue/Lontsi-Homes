using System;

namespace Common.CommunicationModels
{
    /// <summary>
    /// Data transfer object representing a member associated with a tenancy.
    /// Includes role and basic user information for display.
    /// </summary>
    public class TenancyMemberDto
    {
        public int Id { get; set; }
        public int TenancyId { get; set; }

        public string MemberId { get; set; } = string.Empty; // Id du user (ApplicationUser.Id)

        public string Role { get; set; } = string.Empty;

        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string CountryCode { get; set; } = string.Empty;
        public string PhoneNumber { get; set; } = string.Empty;
        public string WhatsAppPhoneNumber { get; set; } = string.Empty;
        public bool EmailConfirmed { get; set; }
        public bool WhatsAppPhoneVerified { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }
}
