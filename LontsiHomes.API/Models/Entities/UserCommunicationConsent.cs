namespace LontsiHomes.API.Models.Entities;

public sealed class UserCommunicationConsent
{
    public long Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Status { get; set; } = CommunicationConsentStatuses.Granted;
    public string PhoneNumberE164 { get; set; } = string.Empty;
    public string TextVersion { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset? GrantedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}
