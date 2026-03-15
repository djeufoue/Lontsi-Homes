using Microsoft.AspNetCore.Identity;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Email;
using System.Security.Cryptography;

namespace RentHub.API.Services.Users
{
    public class UserOnboardingService : IUserOnboardingService
    {
        private const string OtpLoginProvider = "RentHub";
        private const string ActivationOtpTokenName = "ActivationOtpCode";
        private const string ActivationOtpExpiryTokenName = "ActivationOtpExpiryUnix";

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;

        public UserOnboardingService(
            UserManager<ApplicationUser> userManager,
            IEmailService emailService,
            IConfiguration configuration)
        {
            _userManager = userManager;
            _emailService = emailService;
            _configuration = configuration;
        }

        public async Task<InvitedUserResult> EnsureUserAsync(
            string email,
            string? fullName,
            string? countryCode,
            string? phoneNumber,
            string roleName)
        {
            var normalizedEmail = (email ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedEmail))
            {
                throw new InvalidOperationException("Email is required.");
            }

            var user = await _userManager.FindByEmailAsync(normalizedEmail);
            var isNewUser = false;
            string? temporaryPassword = null;

            if (user == null)
            {
                user = new ApplicationUser
                {
                    UserName = normalizedEmail,
                    Email = normalizedEmail,
                    FullName = (fullName ?? string.Empty).Trim(),
                    CountryCode = (countryCode ?? string.Empty).Trim(),
                    PhoneNumber = (phoneNumber ?? string.Empty).Trim(),
                    EmailConfirmed = false
                };

                temporaryPassword = GenerateTemporaryPassword();
                var createResult = await _userManager.CreateAsync(user, temporaryPassword);
                if (!createResult.Succeeded)
                {
                    throw new InvalidOperationException(string.Join("; ", createResult.Errors.Select(error => error.Description)));
                }

                isNewUser = true;
            }
            else
            {
                var didUpdate = false;
                var trimmedFullName = (fullName ?? string.Empty).Trim();
                var trimmedCountryCode = (countryCode ?? string.Empty).Trim();
                var trimmedPhoneNumber = (phoneNumber ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(user.FullName) && !string.IsNullOrWhiteSpace(trimmedFullName))
                {
                    user.FullName = trimmedFullName;
                    didUpdate = true;
                }

                if (string.IsNullOrWhiteSpace(user.CountryCode) && !string.IsNullOrWhiteSpace(trimmedCountryCode))
                {
                    user.CountryCode = trimmedCountryCode;
                    didUpdate = true;
                }

                if (string.IsNullOrWhiteSpace(user.PhoneNumber) && !string.IsNullOrWhiteSpace(trimmedPhoneNumber))
                {
                    user.PhoneNumber = trimmedPhoneNumber;
                    didUpdate = true;
                }

                if (didUpdate)
                {
                    var updateResult = await _userManager.UpdateAsync(user);
                    if (!updateResult.Succeeded)
                    {
                        throw new InvalidOperationException(string.Join("; ", updateResult.Errors.Select(error => error.Description)));
                    }
                }
            }

            if (!await _userManager.IsInRoleAsync(user, roleName))
            {
                var roleResult = await _userManager.AddToRoleAsync(user, roleName);
                if (!roleResult.Succeeded)
                {
                    throw new InvalidOperationException(string.Join("; ", roleResult.Errors.Select(error => error.Description)));
                }
            }

            if (isNewUser)
            {
                await SendActivationOtpAsync(user, temporaryPassword, roleName);
            }

            return new InvitedUserResult
            {
                User = user,
                IsNewUser = isNewUser,
                TemporaryPassword = temporaryPassword
            };
        }

        public async Task SendActivationOtpAsync(
            ApplicationUser user,
            string? temporaryPassword = null,
            string? welcomeRoleLabel = null)
        {
            var otp = GenerateOtpCode();
            var expiry = DateTimeOffset.UtcNow.AddMinutes(10);

            await _userManager.SetAuthenticationTokenAsync(user, OtpLoginProvider, ActivationOtpTokenName, otp);
            await _userManager.SetAuthenticationTokenAsync(user, OtpLoginProvider, ActivationOtpExpiryTokenName, expiry.ToUnixTimeSeconds().ToString());

            var subject = string.IsNullOrWhiteSpace(temporaryPassword)
                ? "RentHub Account Activation OTP"
                : "Welcome to RentHub";

            var greetingName = string.IsNullOrWhiteSpace(user.FullName) ? "there" : user.FullName;
            var verifyUrl = BuildVerifyUrl(user.Email ?? string.Empty);
            var lines = new List<string>
            {
                $"Hello {greetingName},",
                string.Empty
            };

            if (!string.IsNullOrWhiteSpace(temporaryPassword))
            {
                lines.Add(string.IsNullOrWhiteSpace(welcomeRoleLabel)
                    ? "A RentHub account has been created for you."
                    : $"A RentHub account has been created for you and linked to the {welcomeRoleLabel} role.");
                lines.Add("To activate your account:");
                lines.Add("1. Open the RentHub account verification page.");
                if (!string.IsNullOrWhiteSpace(verifyUrl))
                {
                    lines.Add($"   {verifyUrl}");
                }
                lines.Add($"2. Enter your email address: {user.Email}");
                lines.Add($"3. Enter this OTP code: {otp}");
                lines.Add($"4. Sign in with this temporary password: {temporaryPassword}");
                lines.Add("5. After activation, change your password as soon as possible.");
                lines.Add(string.Empty);
            }
            else
            {
                lines.Add($"Your RentHub activation OTP is: {otp}");
                lines.Add("Use it on the account verification page to activate your account.");
                if (!string.IsNullOrWhiteSpace(verifyUrl))
                {
                    lines.Add(verifyUrl);
                }
                lines.Add(string.Empty);
            }

            lines.Add("This OTP expires in 10 minutes.");
            lines.Add(string.Empty);
            lines.Add("If you did not expect this message, please ignore it.");

            await _emailService.SendEmailAsync(user.Email ?? string.Empty, subject, string.Join(Environment.NewLine, lines));
        }

        private string? BuildVerifyUrl(string email)
        {
            var portalBaseUrl = _configuration["Portal:BaseUrl"]?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(portalBaseUrl))
            {
                return null;
            }

            return $"{portalBaseUrl}/Auth/VerifyAccount?email={Uri.EscapeDataString(email)}";
        }

        private static string GenerateOtpCode()
        {
            var value = RandomNumberGenerator.GetInt32(0, 1000000);
            return value.ToString("D6");
        }

        private static string GenerateTemporaryPassword()
        {
            var letters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
            Span<char> chars = stackalloc char[10];
            for (var index = 0; index < 10; index++)
            {
                chars[index] = letters[RandomNumberGenerator.GetInt32(letters.Length)];
            }

            return $"Rh!{new string(chars)}9a";
        }
    }
}
