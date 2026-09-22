using Common.CommunicationModels;
using LontsiHomes.API.Models.Entities;

namespace LontsiHomes.API.Helpers;

public static class WhatsAppActivationStatus
{
    public static AdminWhatsAppActivationDto Build(
        ApplicationUser user, bool hasConsent, bool wasRevoked,
        bool hasPendingCode, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        var pending = !string.IsNullOrWhiteSpace(user.PendingWhatsAppPhoneNumber);
        var number = pending ? user.PendingWhatsAppPhoneNumber : user.WhatsAppPhoneNumber;
        var hasNumber = !string.IsNullOrWhiteSpace(number);
        var verified = hasNumber && !pending && user.IsWhatsAppPhoneVerified;
        var active = verified && hasConsent;
        var state = !hasNumber ? "missing"
            : pending ? hasPendingCode
                ? expiresAt.HasValue && expiresAt.Value > now ? "pending" : "expired"
                : "proposed"
            : active ? "active"
            : wasRevoked ? "revoked"
            : verified ? "consent-missing"
            : "proposed";

        return new AdminWhatsAppActivationDto
        {
            State = state,
            PhoneNumber = number,
            HasNumber = hasNumber,
            IsVerified = verified,
            HasConsent = active,
            VerifiedAt = verified ? user.WhatsAppPhoneVerifiedAt : null,
            CodeExpiresAt = pending && hasPendingCode ? expiresAt : null
        };
    }
}
