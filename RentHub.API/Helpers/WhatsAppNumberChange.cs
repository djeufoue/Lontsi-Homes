using RentHub.API.Models.Entities;

namespace RentHub.API.Helpers;

public static class WhatsAppNumberChange
{
    public static bool RequiresVerification(ApplicationUser user, string normalizedNumber, bool hasTransactionalConsent)
        => !user.IsWhatsAppPhoneVerified || !hasTransactionalConsent ||
           !string.Equals(user.NormalizedWhatsAppPhoneNumber, normalizedNumber, StringComparison.Ordinal);

    // Keep the currently verified destination and its consent until the replacement passes OTP.
    public static void Propose(ApplicationUser user, string normalizedNumber)
        => user.PendingWhatsAppPhoneNumber = normalizedNumber;

    public static void Cancel(ApplicationUser user) => user.PendingWhatsAppPhoneNumber = null;

    public static void Confirm(ApplicationUser user, bool usePrimaryPhone, DateTimeOffset verifiedAt)
    {
        var number = user.PendingWhatsAppPhoneNumber;
        if (string.IsNullOrWhiteSpace(number))
            throw new InvalidOperationException("No WhatsApp number is pending verification.");

        user.UsePrimaryPhoneForWhatsApp = usePrimaryPhone;
        user.WhatsAppPhoneNumber = number;
        user.NormalizedWhatsAppPhoneNumber = number;
        user.PendingWhatsAppPhoneNumber = null;
        user.IsWhatsAppPhoneVerified = true;
        user.WhatsAppPhoneVerifiedAt = verifiedAt;
    }

    public static string OtpSubject(string userId, string normalizedNumber) => $"{userId}:{normalizedNumber}";
}
