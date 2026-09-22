using LontsiHomes.API.Models.Entities;

namespace LontsiHomes.API.Services.Users
{
    public interface IUserOnboardingService
    {
        Task<InvitedUserResult> EnsureUserAsync(
            string email,
            string? fullName,
            string? countryCode,
            string? phoneNumber,
            string? whatsAppPhoneNumber,
            string roleName,
            bool sendActivationEmail = true);

        Task SendActivationOtpAsync(
            ApplicationUser user,
            string? temporaryPassword = null,
            string? welcomeRoleLabel = null);

        Task SendEmailChangeVerificationOtpAsync(ApplicationUser user);

        Task SendLandlordEmailOtpAsync(ApplicationUser user);

        Task SendLandlordPhoneOtpAsync(ApplicationUser user);

        Task SendLandlordMobilePaymentOtpsAsync(
            ApplicationUser user,
            bool sendSubscriptionPaymentOtp,
            bool sendPayoutOtp,
            bool sendWhatsAppOtp);

        Task SendVisitorActivationOtpAsync(ApplicationUser user);

        Task<OtpSendThrottleStatus> GetOtpThrottleStatusAsync(ApplicationUser user, string purpose);
    }

    public static class OtpSendPurposes
    {
        public const string LandlordPhone = "landlord-phone";
        public const string SubscriptionPaymentPhone = "subscription-payment-phone";
        public const string RentPayoutPhone = "rent-payout-phone";
        public const string WhatsAppPhone = "whatsapp-phone";
        public const string VisitorPhone = "visitor-phone";
        public const string VisitorWhatsApp = "visitor-whatsapp";
    }

    public sealed class OtpSendThrottleStatus
    {
        public int DailyRequestLimit { get; init; }
        public int DailyRequestsRemaining { get; init; }
        public int RetryAfterSeconds { get; init; }
        public bool DailyLimitReached => DailyRequestsRemaining <= 0;
        public DateTimeOffset? NextAllowedAt { get; init; }
        public DateTimeOffset DailyLimitResetsAt { get; init; }
    }

    public sealed class OtpSendThrottledException : InvalidOperationException
    {
        public OtpSendThrottledException(string message, OtpSendThrottleStatus status)
            : base(message)
        {
            Status = status;
        }

        public OtpSendThrottleStatus Status { get; }
    }

    public sealed class InvitedUserResult
    {
        public required ApplicationUser User { get; init; }
        public bool IsNewUser { get; init; }
        public string? TemporaryPassword { get; init; }
    }
}
