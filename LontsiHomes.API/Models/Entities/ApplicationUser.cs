using Microsoft.AspNetCore.Identity;
using Common.Enums;

namespace LontsiHomes.API.Models.Entities
{
    /// <summary>
    /// Represents an authenticated user in the system.  We use long as the primary key type
    /// to allow for future scalability. Additional profile fields can be added as needed.
    /// </summary>
    public class ApplicationUser : IdentityUser
    {
        public string? FullName { get; set; }
        public string? CountryCode { get; set; }
        public string? CountryIsoCode { get; set; }
        public bool UsePrimaryPhoneForSubscriptionPayments { get; set; }
        public string? SubscriptionPaymentPhoneNumber { get; set; }
        public PayoutChannelEnum? SubscriptionPaymentChannel { get; set; }
        public bool IsSubscriptionPaymentPhoneVerified { get; set; }
        public DateTimeOffset? SubscriptionPaymentPhoneVerifiedAt { get; set; }
        public bool UsePrimaryPhoneForRentPayouts { get; set; }
        public string? PayoutPhoneNumber { get; set; }
        public PayoutChannelEnum? PayoutChannel { get; set; }
        public bool IsPayoutPhoneVerified { get; set; }
        public DateTimeOffset? PayoutPhoneVerifiedAt { get; set; }
        public bool UsePrimaryPhoneForWhatsApp { get; set; }
        public string? PendingWhatsAppPhoneNumber { get; set; }
        public string? WhatsAppPhoneNumber { get; set; }
        public string? NormalizedWhatsAppPhoneNumber { get; set; }
        public bool IsWhatsAppPhoneVerified { get; set; }
        public DateTimeOffset? WhatsAppPhoneVerifiedAt { get; set; }
        public bool PlatformTermsAccepted { get; set; }
        public DateTimeOffset? PlatformTermsAcceptedAt { get; set; }
        public string? PlatformTermsSignatureName { get; set; }
        public string? PlatformTermsVersion { get; set; }
        public string? StripeConnectAccountId { get; set; }
        public bool StripePayoutDetailsSubmitted { get; set; }
        public bool StripeChargesEnabled { get; set; }
        public bool StripePayoutsEnabled { get; set; }
        public string? StripePayoutRequirementsSummary { get; set; }
        public string? StripePayoutDisabledReason { get; set; }
        public DateTimeOffset? StripePayoutSetupStartedAt { get; set; }
        public DateTimeOffset? StripePayoutSetupCompletedAt { get; set; }
        public DateTimeOffset? StripePayoutStatusUpdatedAt { get; set; }
        public bool IsSubscriptionExempt { get; set; }
        public PlatformLanguage Language { get; set; } = PlatformLanguage.English;
        public PlatformLanguage EmailLanguage { get; set; } = PlatformLanguage.English;
        public bool ConversationEmailNotificationsEnabled { get; set; } = true;
        public DateTimeOffset? SessionInvalidatedAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        // Navigation properties
        public ICollection<Property> OwnedProperties { get; set; } = new List<Property>();
        public LandlordKycProfile? KycProfile { get; set; }
        public ICollection<UserCommunicationConsent> CommunicationConsents { get; set; } = new List<UserCommunicationConsent>();
        public ICollection<NotificationDelivery> NotificationDeliveries { get; set; } = new List<NotificationDelivery>();
    }
}
