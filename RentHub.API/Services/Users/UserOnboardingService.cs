using Microsoft.AspNetCore.Identity;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Email;
using RentHub.API.Services.Sms;
using System.Security.Cryptography;

namespace RentHub.API.Services.Users
{
    public class UserOnboardingService : IUserOnboardingService
    {
        private const string OtpLoginProvider = "RentHub";
        private const string ActivationOtpTokenName = "ActivationOtpCode";
        private const string ActivationOtpExpiryTokenName = "ActivationOtpExpiryUnix";
        private const string PhoneOtpTokenName = "PhoneOtpCode";
        private const string PhoneOtpExpiryTokenName = "PhoneOtpExpiryUnix";
        private const string PayoutOtpTokenName = "PayoutOtpCode";
        private const string PayoutOtpExpiryTokenName = "PayoutOtpExpiryUnix";
        private const string WhatsAppOtpTokenName = "WhatsAppOtpCode";
        private const string WhatsAppOtpExpiryTokenName = "WhatsAppOtpExpiryUnix";

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IEmailService _emailService;
        private readonly ISmsService _smsService;
        private readonly IConfiguration _configuration;

        public UserOnboardingService(
            UserManager<ApplicationUser> userManager,
            IEmailService emailService,
            ISmsService smsService,
            IConfiguration configuration)
        {
            _userManager = userManager;
            _emailService = emailService;
            _smsService = smsService;
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
            await SendLandlordActivationOtpInternalAsync(user, temporaryPassword, welcomeRoleLabel);
        }

        public async Task SendVisitorActivationOtpAsync(ApplicationUser user)
        {
            var otp = GenerateOtpCode();
            var phoneOtp = GenerateOtpCode();
            var whatsAppOtp = GenerateOtpCode();
            var expiry = DateTimeOffset.UtcNow.AddMinutes(10);

            await StoreOtpAsync(user, ActivationOtpTokenName, ActivationOtpExpiryTokenName, otp, expiry);
            await StoreOtpAsync(user, PhoneOtpTokenName, PhoneOtpExpiryTokenName, phoneOtp, expiry);
            await StoreOtpAsync(user, WhatsAppOtpTokenName, WhatsAppOtpExpiryTokenName, whatsAppOtp, expiry);

            var verifyUrl = BuildVerifyUrl(user.Email ?? string.Empty, isVisitor: true);
            var greetingName = string.IsNullOrWhiteSpace(user.FullName) ? "there" : user.FullName;
            var lines = new List<string>
            {
                $"Hello {greetingName},",
                string.Empty,
                $"Your RentHub visitor email OTP is: {otp}",
                $"Your RentHub visitor phone OTP is: {phoneOtp}",
                $"Your RentHub visitor WhatsApp OTP is: {whatsAppOtp}",
                "Use all three codes to activate your visitor account and start private conversations with landlords.",
                string.Empty,
                "Verification page:"
            };

            if (!string.IsNullOrWhiteSpace(verifyUrl))
            {
                lines.Add(verifyUrl);
            }

            lines.Add(string.Empty);
            lines.Add("This OTP expires in 10 minutes.");
            lines.Add(string.Empty);
            lines.Add("If you did not request this account, please ignore this message.");

            await _emailService.SendEmailAsync(
                user.Email ?? string.Empty,
                "RentHub Visitor Account Verification",
                string.Join(Environment.NewLine, lines));

            await _smsService.SendSmsAsync(
                user.PhoneNumber ?? string.Empty,
                $"RentHub visitor phone OTP: {phoneOtp}. This code expires in 10 minutes.");

            await _smsService.SendWhatsAppAsync(
                user.WhatsAppPhoneNumber ?? string.Empty,
                $"RentHub visitor WhatsApp OTP: {whatsAppOtp}. This code expires in 10 minutes.");
        }

        private async Task SendLandlordActivationOtpInternalAsync(
            ApplicationUser user,
            string? temporaryPassword = null,
            string? welcomeRoleLabel = null)
        {
            var otp = GenerateOtpCode();
            var expiry = DateTimeOffset.UtcNow.AddMinutes(10);

            await StoreOtpAsync(user, ActivationOtpTokenName, ActivationOtpExpiryTokenName, otp, expiry);

            string? payoutOtp = null;
            if (!string.IsNullOrWhiteSpace(user.PayoutPhoneNumber))
            {
                payoutOtp = GenerateOtpCode();
                await StoreOtpAsync(user, PayoutOtpTokenName, PayoutOtpExpiryTokenName, payoutOtp, expiry);
            }

            string? whatsAppOtp = null;
            if (!string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber))
            {
                whatsAppOtp = GenerateOtpCode();
                await StoreOtpAsync(user, WhatsAppOtpTokenName, WhatsAppOtpExpiryTokenName, whatsAppOtp, expiry);
            }

            var subject = string.IsNullOrWhiteSpace(temporaryPassword)
                ? "RentHub Account Activation OTP"
                : "Welcome to RentHub";

            var greetingName = string.IsNullOrWhiteSpace(user.FullName) ? "there" : user.FullName;
            var verifyUrl = BuildVerifyUrl(user.Email ?? string.Empty, isVisitor: false);
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
                lines.Add($"3. Enter this email OTP code: {otp}");
                if (!string.IsNullOrWhiteSpace(user.PayoutPhoneNumber))
                {
                    lines.Add($"4. Enter the payout-number OTP sent to {user.PayoutPhoneNumber}.");
                }
                if (!string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber))
                {
                    lines.Add($"5. Enter the WhatsApp OTP sent to {user.WhatsAppPhoneNumber}.");
                }
                lines.Add($"6. Sign in with this temporary password: {temporaryPassword}");
                lines.Add("7. After activation, change your password as soon as possible.");
                lines.Add(string.Empty);
            }
            else
            {
                lines.Add($"Your RentHub email OTP is: {otp}");
                lines.Add("Use it on the account verification page together with the payout-number and WhatsApp OTPs.");
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

            if (!string.IsNullOrWhiteSpace(payoutOtp) && !string.IsNullOrWhiteSpace(user.PayoutPhoneNumber))
            {
                await _smsService.SendSmsAsync(
                    user.PayoutPhoneNumber,
                    $"RentHub payout verification OTP: {payoutOtp}. This code expires in 10 minutes.");
            }

            if (!string.IsNullOrWhiteSpace(whatsAppOtp) && !string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber))
            {
                await _smsService.SendWhatsAppAsync(
                    user.WhatsAppPhoneNumber,
                    $"RentHub WhatsApp verification OTP: {whatsAppOtp}. This code expires in 10 minutes.");
            }
        }

        private string? BuildVerifyUrl(string email, bool isVisitor)
        {
            var portalBaseUrl = _configuration["Portal:BaseUrl"]?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(portalBaseUrl))
            {
                return null;
            }

            var path = isVisitor ? "/Auth/VerifyVisitorAccount" : "/Auth/VerifyAccount";
            return $"{portalBaseUrl}{path}?email={Uri.EscapeDataString(email)}";
        }

        private static string GenerateOtpCode()
        {
            var value = RandomNumberGenerator.GetInt32(0, 1000000);
            return value.ToString("D6");
        }

        private async Task StoreOtpAsync(
            ApplicationUser user,
            string valueTokenName,
            string expiryTokenName,
            string otp,
            DateTimeOffset expiry)
        {
            await _userManager.SetAuthenticationTokenAsync(user, OtpLoginProvider, valueTokenName, otp);
            await _userManager.SetAuthenticationTokenAsync(user, OtpLoginProvider, expiryTokenName, expiry.ToUnixTimeSeconds().ToString());
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
