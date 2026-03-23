using RentHub.API.Models.Entities;

namespace RentHub.API.Services.Users
{
    public interface IUserOnboardingService
    {
        Task<InvitedUserResult> EnsureUserAsync(
            string email,
            string? fullName,
            string? countryCode,
            string? phoneNumber,
            string roleName);

        Task SendActivationOtpAsync(
            ApplicationUser user,
            string? temporaryPassword = null,
            string? welcomeRoleLabel = null);

        Task SendVisitorActivationOtpAsync(ApplicationUser user);
    }

    public sealed class InvitedUserResult
    {
        public required ApplicationUser User { get; init; }
        public bool IsNewUser { get; init; }
        public string? TemporaryPassword { get; init; }
    }
}
