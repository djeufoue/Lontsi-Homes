using Common.Enums;
using Common.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
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
        private const string SubscriptionPaymentOtpTokenName = "SubscriptionPaymentOtpCode";
        private const string SubscriptionPaymentOtpExpiryTokenName = "SubscriptionPaymentOtpExpiryUnix";
        private const string PayoutOtpTokenName = "PayoutOtpCode";
        private const string PayoutOtpExpiryTokenName = "PayoutOtpExpiryUnix";
        private const string WhatsAppOtpTokenName = "WhatsAppOtpCode";
        private const string WhatsAppOtpExpiryTokenName = "WhatsAppOtpExpiryUnix";
        private const int TwilioOtpDailyRequestLimit = 2;
        private static readonly TimeSpan TwilioOtpCooldown = TimeSpan.FromSeconds(60);

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _emailService;
        private readonly ISmsService _smsService;
        private readonly IConfiguration _configuration;

        public UserOnboardingService(
            UserManager<ApplicationUser> userManager,
            ApplicationDbContext context,
            IEmailService emailService,
            ISmsService smsService,
            IConfiguration configuration)
        {
            _userManager = userManager;
            _context = context;
            _emailService = emailService;
            _smsService = smsService;
            _configuration = configuration;
        }

        public async Task<InvitedUserResult> EnsureUserAsync(
            string email,
            string? fullName,
            string? countryCode,
            string? phoneNumber,
            string? whatsAppPhoneNumber,
            string roleName,
            bool sendActivationEmail = true)
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
                    CountryCode = PhoneNumberHelper.NormalizeOrEmpty(countryCode),
                    PhoneNumber = PhoneNumberHelper.NormalizeOrEmpty(phoneNumber),
                    WhatsAppPhoneNumber = PhoneNumberHelper.NormalizeOrEmpty(whatsAppPhoneNumber),
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
                var trimmedCountryCode = PhoneNumberHelper.NormalizeOrEmpty(countryCode);
                var trimmedPhoneNumber = PhoneNumberHelper.NormalizeOrEmpty(phoneNumber);
                var trimmedWhatsAppPhoneNumber = PhoneNumberHelper.NormalizeOrEmpty(whatsAppPhoneNumber);

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

                if (string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber) && !string.IsNullOrWhiteSpace(trimmedWhatsAppPhoneNumber))
                {
                    user.WhatsAppPhoneNumber = trimmedWhatsAppPhoneNumber;
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

            if (isNewUser && sendActivationEmail)
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

        public Task SendEmailChangeVerificationOtpAsync(ApplicationUser user)
        {
            return SendLandlordActivationOtpInternalAsync(
                user,
                temporaryPassword: null,
                welcomeRoleLabel: null,
                useGeneralAccountPage: true,
                includePrimaryPhoneVerification: true);
        }

        public async Task SendLandlordEmailOtpAsync(ApplicationUser user)
        {
            var otp = GenerateOtpCode();
            var expiry = DateTimeOffset.UtcNow.AddMinutes(10);

            await StoreOtpAsync(user, ActivationOtpTokenName, ActivationOtpExpiryTokenName, otp, expiry);

            var isFrench = user.EmailLanguage == PlatformLanguage.French;
            var greetingName = string.IsNullOrWhiteSpace(user.FullName) ? (isFrench ? "" : "there") : user.FullName;
            var verifyUrl = BuildVerifyUrl(user.Email ?? string.Empty, isVisitor: false);
            var lines = new List<string>
            {
                isFrench ? $"Bonjour {greetingName}," : $"Hello {greetingName},",
                string.Empty,
                isFrench ? $"Votre code de vérification par courriel Lontsi Homes est : {otp}" : $"Your Lontsi Homes email OTP is: {otp}",
                isFrench ? "Utilisez-le pour confirmer votre adresse courriel et poursuivre votre inscription comme bailleur." : "Use it to confirm your email and continue landlord registration.",
                string.Empty
            };

            if (!string.IsNullOrWhiteSpace(verifyUrl))
            {
                lines.Add(isFrench ? "Page de vérification :" : "Verification page:");
                lines.Add(verifyUrl);
                lines.Add(string.Empty);
            }

            lines.Add(isFrench ? "Ce code expire dans 10 minutes." : "This OTP expires in 10 minutes.");
            lines.Add(string.Empty);
            lines.Add(isFrench ? "Si vous n’avez pas demandé ce compte, ignorez ce message." : "If you did not request this account, please ignore this message.");

            await _emailService.SendEmailAsync(
                user.Email ?? string.Empty,
                isFrench ? "Code de vérification de l’adresse courriel Lontsi Homes" : "Lontsi Homes Email Verification OTP",
                string.Join(Environment.NewLine, lines));
        }

        public async Task SendLandlordPhoneOtpAsync(ApplicationUser user)
        {
            if (string.IsNullOrWhiteSpace(user.PhoneNumber))
            {
                return;
            }

            var phoneOtp = GenerateOtpCode();
            var expiry = DateTimeOffset.UtcNow.AddMinutes(10);

            var recipient = BuildInternationalPhoneNumber(user.CountryCode, user.PhoneNumber);
            await AssertTwilioOtpSendAllowedAsync(user, OtpSendPurposes.LandlordPhone, recipient);
            await RecordTwilioOtpSendAsync(user, OtpSendPurposes.LandlordPhone, "SMS", recipient);

            await StoreOtpAsync(user, PhoneOtpTokenName, PhoneOtpExpiryTokenName, phoneOtp, expiry);

            await _smsService.SendSmsAsync(
                recipient,
                $"Lontsi Homes phone verification OTP: {phoneOtp}. This code expires in 10 minutes.");
        }

        public async Task SendLandlordMobilePaymentOtpsAsync(
            ApplicationUser user,
            bool sendSubscriptionPaymentOtp,
            bool sendPayoutOtp,
            bool sendWhatsAppOtp)
        {
            var plannedSends = new List<PlannedOtpSend>();
            if (sendSubscriptionPaymentOtp && !string.IsNullOrWhiteSpace(user.SubscriptionPaymentPhoneNumber))
            {
                plannedSends.Add(new PlannedOtpSend(
                    OtpSendPurposes.SubscriptionPaymentPhone,
                    "SMS",
                    BuildInternationalPhoneNumber(user.CountryCode, user.SubscriptionPaymentPhoneNumber),
                    SubscriptionPaymentOtpTokenName,
                    SubscriptionPaymentOtpExpiryTokenName,
                    "Lontsi Homes subscription payment OTP"));
            }

            if (sendPayoutOtp && !string.IsNullOrWhiteSpace(user.PayoutPhoneNumber))
            {
                plannedSends.Add(new PlannedOtpSend(
                    OtpSendPurposes.RentPayoutPhone,
                    "SMS",
                    BuildInternationalPhoneNumber(user.CountryCode, user.PayoutPhoneNumber),
                    PayoutOtpTokenName,
                    PayoutOtpExpiryTokenName,
                    "Lontsi Homes rent payout OTP"));
            }

            if (sendWhatsAppOtp && !string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber))
            {
                plannedSends.Add(new PlannedOtpSend(
                    OtpSendPurposes.WhatsAppPhone,
                    "WhatsApp",
                    BuildInternationalPhoneNumber(user.CountryCode, user.WhatsAppPhoneNumber),
                    WhatsAppOtpTokenName,
                    WhatsAppOtpExpiryTokenName,
                    "Lontsi Homes WhatsApp verification OTP"));
            }

            foreach (var plannedSend in plannedSends)
            {
                await AssertTwilioOtpSendAllowedAsync(user, plannedSend.Purpose, plannedSend.Recipient);
            }

            await RecordTwilioOtpSendsAsync(user, plannedSends);

            var expiry = DateTimeOffset.UtcNow.AddMinutes(10);

            foreach (var plannedSend in plannedSends)
            {
                var otp = GenerateOtpCode();
                await StoreOtpAsync(user, plannedSend.ValueTokenName, plannedSend.ExpiryTokenName, otp, expiry);
                var message = $"{plannedSend.MessagePrefix}: {otp}. This code expires in 10 minutes.";
                if (plannedSend.Channel == "WhatsApp")
                {
                    await _smsService.SendWhatsAppAsync(plannedSend.Recipient, message);
                }
                else
                {
                    await _smsService.SendSmsAsync(plannedSend.Recipient, message);
                }
            }
        }

        public async Task SendVisitorActivationOtpAsync(ApplicationUser user)
        {
            var otp = GenerateOtpCode();
            var phoneOtp = string.IsNullOrWhiteSpace(user.PhoneNumber) ? null : GenerateOtpCode();
            var whatsAppOtp = string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber) ? null : GenerateOtpCode();
            var expiry = DateTimeOffset.UtcNow.AddMinutes(10);

            await StoreOtpAsync(user, ActivationOtpTokenName, ActivationOtpExpiryTokenName, otp, expiry);
            if (phoneOtp != null)
            {
                await StoreOtpAsync(user, PhoneOtpTokenName, PhoneOtpExpiryTokenName, phoneOtp, expiry);
            }

            if (whatsAppOtp != null)
            {
                await StoreOtpAsync(user, WhatsAppOtpTokenName, WhatsAppOtpExpiryTokenName, whatsAppOtp, expiry);
            }

            var verifyUrl = BuildVerifyUrl(user.Email ?? string.Empty, isVisitor: true);
            var isFrench = user.EmailLanguage == PlatformLanguage.French;
            var greetingName = string.IsNullOrWhiteSpace(user.FullName) ? (isFrench ? "" : "there") : user.FullName;
            var lines = new List<string>
            {
                isFrench ? $"Bonjour {greetingName}," : $"Hello {greetingName},",
                string.Empty,
                isFrench ? $"Votre code de vérification visiteur par courriel est : {otp}" : $"Your RentHub visitor email OTP is: {otp}",
            };

            if (phoneOtp != null)
            {
                lines.Add(isFrench ? $"Votre code de vérification visiteur par téléphone est : {phoneOtp}" : $"Your RentHub visitor phone OTP is: {phoneOtp}");
            }

            if (whatsAppOtp != null)
            {
                lines.Add(isFrench ? $"Votre code de vérification visiteur WhatsApp est : {whatsAppOtp}" : $"Your RentHub visitor WhatsApp OTP is: {whatsAppOtp}");
            }

            lines.Add(isFrench ? "Utilisez les codes ci-dessus pour activer votre compte visiteur." : "Use the verification codes shown above to activate your visitor account.");
            lines.Add(string.Empty);
            lines.Add(isFrench ? "Page de vérification :" : "Verification page:");

            if (!string.IsNullOrWhiteSpace(verifyUrl))
            {
                lines.Add(verifyUrl);
            }

            lines.Add(string.Empty);
            lines.Add(isFrench ? "Ces codes expirent dans 10 minutes." : "This OTP expires in 10 minutes.");
            lines.Add(string.Empty);
            lines.Add(isFrench ? "Si vous n’avez pas demandé ce compte, ignorez ce message." : "If you did not request this account, please ignore this message.");

            await _emailService.SendEmailAsync(
                user.Email ?? string.Empty,
                isFrench ? "Vérification du compte visiteur Lontsi Homes" : "RentHub Visitor Account Verification",
                string.Join(Environment.NewLine, lines));

            if (phoneOtp != null)
            {
                var phoneRecipient = BuildInternationalPhoneNumber(user.CountryCode, user.PhoneNumber);
                await AssertTwilioOtpSendAllowedAsync(user, OtpSendPurposes.VisitorPhone, phoneRecipient);
                await RecordTwilioOtpSendAsync(user, OtpSendPurposes.VisitorPhone, "SMS", phoneRecipient);
                await _smsService.SendSmsAsync(
                    phoneRecipient,
                    $"RentHub visitor phone OTP: {phoneOtp}. This code expires in 10 minutes.");
            }

            if (whatsAppOtp != null)
            {
                var whatsAppRecipient = BuildInternationalPhoneNumber(user.CountryCode, user.WhatsAppPhoneNumber);
                await AssertTwilioOtpSendAllowedAsync(user, OtpSendPurposes.VisitorWhatsApp, whatsAppRecipient);
                await RecordTwilioOtpSendAsync(user, OtpSendPurposes.VisitorWhatsApp, "WhatsApp", whatsAppRecipient);
                await _smsService.SendWhatsAppAsync(
                    whatsAppRecipient,
                    $"RentHub visitor WhatsApp OTP: {whatsAppOtp}. This code expires in 10 minutes.");
            }
        }

        private async Task SendLandlordActivationOtpInternalAsync(
            ApplicationUser user,
            string? temporaryPassword = null,
            string? welcomeRoleLabel = null,
            bool useGeneralAccountPage = false,
            bool includePrimaryPhoneVerification = false)
        {
            var isTenantActivation = string.Equals(welcomeRoleLabel, "Tenant", StringComparison.OrdinalIgnoreCase);
            var otp = GenerateOtpCode();
            var expiry = DateTimeOffset.UtcNow.AddMinutes(10);

            string? payoutOtp = null;
            var plannedSends = new List<PlannedOtpSend>();
            if (includePrimaryPhoneVerification && !string.IsNullOrWhiteSpace(user.PhoneNumber))
            {
                plannedSends.Add(new PlannedOtpSend(
                    OtpSendPurposes.LandlordPhone,
                    "SMS",
                    BuildInternationalPhoneNumber(user.CountryCode, user.PhoneNumber),
                    PhoneOtpTokenName,
                    PhoneOtpExpiryTokenName,
                    "RentHub phone verification OTP"));
            }
            if (!isTenantActivation && !string.IsNullOrWhiteSpace(user.PayoutPhoneNumber))
            {
                plannedSends.Add(new PlannedOtpSend(
                    OtpSendPurposes.RentPayoutPhone,
                    "SMS",
                    BuildInternationalPhoneNumber(user.CountryCode, user.PayoutPhoneNumber),
                    PayoutOtpTokenName,
                    PayoutOtpExpiryTokenName,
                    "RentHub payout verification OTP"));
            }

            string? subscriptionPaymentOtp = null;
            if (!isTenantActivation && !string.IsNullOrWhiteSpace(user.SubscriptionPaymentPhoneNumber))
            {
                plannedSends.Add(new PlannedOtpSend(
                    OtpSendPurposes.SubscriptionPaymentPhone,
                    "SMS",
                    BuildInternationalPhoneNumber(user.CountryCode, user.SubscriptionPaymentPhoneNumber),
                    SubscriptionPaymentOtpTokenName,
                    SubscriptionPaymentOtpExpiryTokenName,
                    "RentHub subscription payment verification OTP"));
            }

            string? whatsAppOtp = null;
            if (!isTenantActivation && !string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber))
            {
                plannedSends.Add(new PlannedOtpSend(
                    OtpSendPurposes.WhatsAppPhone,
                    "WhatsApp",
                    BuildInternationalPhoneNumber(user.CountryCode, user.WhatsAppPhoneNumber),
                    WhatsAppOtpTokenName,
                    WhatsAppOtpExpiryTokenName,
                    "RentHub WhatsApp verification OTP"));
            }

            foreach (var plannedSend in plannedSends)
            {
                await AssertTwilioOtpSendAllowedAsync(user, plannedSend.Purpose, plannedSend.Recipient);
            }

            await RecordTwilioOtpSendsAsync(user, plannedSends);

            await StoreOtpAsync(user, ActivationOtpTokenName, ActivationOtpExpiryTokenName, otp, expiry);

            foreach (var plannedSend in plannedSends)
            {
                var twilioOtp = GenerateOtpCode();
                await StoreOtpAsync(user, plannedSend.ValueTokenName, plannedSend.ExpiryTokenName, twilioOtp, expiry);

                if (plannedSend.Purpose == OtpSendPurposes.RentPayoutPhone)
                {
                    payoutOtp = twilioOtp;
                }
                else if (plannedSend.Purpose == OtpSendPurposes.SubscriptionPaymentPhone)
                {
                    subscriptionPaymentOtp = twilioOtp;
                }
                else if (plannedSend.Purpose == OtpSendPurposes.WhatsAppPhone)
                {
                    whatsAppOtp = twilioOtp;
                }
            }

            var isFrench = user.EmailLanguage == PlatformLanguage.French;
            var subject = string.IsNullOrWhiteSpace(temporaryPassword)
                ? (isFrench ? "Code d’activation du compte Lontsi Homes" : "RentHub Account Activation OTP")
                : (isFrench ? "Bienvenue sur Lontsi Homes" : "Welcome to RentHub");

            var greetingName = string.IsNullOrWhiteSpace(user.FullName) ? (isFrench ? "" : "there") : user.FullName;
            var verifyUrl = BuildVerifyUrl(
                user.Email ?? string.Empty,
                isVisitor: false,
                useGeneralAccountPage: isTenantActivation || useGeneralAccountPage,
                returnUrl: isTenantActivation ? "/Tenancies" : null);
            var lines = new List<string>
            {
                isFrench ? $"Bonjour {greetingName}," : $"Hello {greetingName},",
                string.Empty
            };

            if (!string.IsNullOrWhiteSpace(temporaryPassword))
            {
                if (isTenantActivation)
                {
                    lines.Add(isFrench ? "Un compte locataire Lontsi Homes a été créé pour vous et lié à une location." : "A RentHub tenant account has been created for you and linked to a tenancy.");
                    lines.Add(isFrench ? "Pour activer votre compte :" : "To activate your account:");
                    lines.Add(isFrench ? "1. Ouvrez la page de vérification du compte Lontsi Homes." : "1. Open the RentHub account verification page.");
                    if (!string.IsNullOrWhiteSpace(verifyUrl))
                    {
                        lines.Add($"   {verifyUrl}");
                    }
                    lines.Add(isFrench ? $"2. Saisissez votre adresse courriel : {user.Email}" : $"2. Enter your email address: {user.Email}");
                    lines.Add(isFrench ? $"3. Saisissez ce code reçu par courriel : {otp}" : $"3. Enter this email OTP code: {otp}");
                    lines.Add(isFrench ? $"4. Connectez-vous avec ce mot de passe temporaire : {temporaryPassword}" : $"4. Sign in with this temporary password: {temporaryPassword}");
                    lines.Add(isFrench ? "5. Après l’activation, modifiez votre mot de passe dès que possible." : "5. After activation, change your password as soon as possible.");
                }
                else
                {
                    lines.Add(isFrench
                        ? (string.IsNullOrWhiteSpace(welcomeRoleLabel) ? "Un compte Lontsi Homes a été créé pour vous." : $"Un compte Lontsi Homes a été créé pour vous avec le rôle {welcomeRoleLabel}.")
                        : (string.IsNullOrWhiteSpace(welcomeRoleLabel) ? "A RentHub account has been created for you." : $"A RentHub account has been created for you and linked to the {welcomeRoleLabel} role."));
                    lines.Add(isFrench ? "Pour activer votre compte :" : "To activate your account:");
                    lines.Add(isFrench ? "1. Ouvrez la page de vérification du compte Lontsi Homes." : "1. Open the RentHub account verification page.");
                    if (!string.IsNullOrWhiteSpace(verifyUrl))
                    {
                        lines.Add($"   {verifyUrl}");
                    }
                    lines.Add(isFrench ? $"2. Saisissez votre adresse courriel : {user.Email}" : $"2. Enter your email address: {user.Email}");
                    lines.Add(isFrench ? $"3. Saisissez ce code reçu par courriel : {otp}" : $"3. Enter this email OTP code: {otp}");
                    if (!string.IsNullOrWhiteSpace(user.PayoutPhoneNumber))
                    {
                        lines.Add(isFrench ? $"4. Saisissez le code envoyé au numéro de versement {user.PayoutPhoneNumber}." : $"4. Enter the payout-number OTP sent to {user.PayoutPhoneNumber}.");
                    }
                    if (!string.IsNullOrWhiteSpace(user.SubscriptionPaymentPhoneNumber))
                    {
                        lines.Add(isFrench ? $"5. Saisissez le code de paiement d’abonnement envoyé à {user.SubscriptionPaymentPhoneNumber}." : $"5. Enter the subscription-payment OTP sent to {user.SubscriptionPaymentPhoneNumber}.");
                    }
                    if (!string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber))
                    {
                        lines.Add(isFrench ? $"6. Saisissez le code WhatsApp envoyé à {user.WhatsAppPhoneNumber}." : $"6. Enter the WhatsApp OTP sent to {user.WhatsAppPhoneNumber}.");
                    }
                    lines.Add(isFrench ? $"7. Connectez-vous avec ce mot de passe temporaire : {temporaryPassword}" : $"7. Sign in with this temporary password: {temporaryPassword}");
                    lines.Add(isFrench ? "8. Après l’activation, modifiez votre mot de passe dès que possible." : "8. After activation, change your password as soon as possible.");
                }
                lines.Add(string.Empty);
            }
            else
            {
                lines.Add(isFrench ? $"Votre code de vérification Lontsi Homes par courriel est : {otp}" : $"Your RentHub email OTP is: {otp}");
                lines.Add(isFrench ? "Utilisez-le sur la page de vérification avec tous les autres codes reçus." : "Use it on the account verification page together with every other OTP you received.");
                if (!string.IsNullOrWhiteSpace(verifyUrl))
                {
                    lines.Add(verifyUrl);
                }
                lines.Add(string.Empty);
            }

            lines.Add(isFrench ? "Ce code expire dans 10 minutes." : "This OTP expires in 10 minutes.");
            lines.Add(string.Empty);
            lines.Add(isFrench ? "Si vous ne vous attendiez pas à recevoir ce message, ignorez-le." : "If you did not expect this message, please ignore it.");

            await _emailService.SendEmailAsync(user.Email ?? string.Empty, subject, string.Join(Environment.NewLine, lines));

            if (!string.IsNullOrWhiteSpace(subscriptionPaymentOtp) && !string.IsNullOrWhiteSpace(user.SubscriptionPaymentPhoneNumber))
            {
                var plannedSend = plannedSends.First(send => send.Purpose == OtpSendPurposes.SubscriptionPaymentPhone);
                await _smsService.SendSmsAsync(plannedSend.Recipient, $"{plannedSend.MessagePrefix}: {subscriptionPaymentOtp}. This code expires in 10 minutes.");
            }

            if (!string.IsNullOrWhiteSpace(payoutOtp) && !string.IsNullOrWhiteSpace(user.PayoutPhoneNumber))
            {
                var plannedSend = plannedSends.First(send => send.Purpose == OtpSendPurposes.RentPayoutPhone);
                await _smsService.SendSmsAsync(plannedSend.Recipient, $"{plannedSend.MessagePrefix}: {payoutOtp}. This code expires in 10 minutes.");
            }

            if (!string.IsNullOrWhiteSpace(whatsAppOtp) && !string.IsNullOrWhiteSpace(user.WhatsAppPhoneNumber))
            {
                var plannedSend = plannedSends.First(send => send.Purpose == OtpSendPurposes.WhatsAppPhone);
                await _smsService.SendWhatsAppAsync(plannedSend.Recipient, $"{plannedSend.MessagePrefix}: {whatsAppOtp}. This code expires in 10 minutes.");
            }
        }

        public async Task<OtpSendThrottleStatus> GetTwilioOtpThrottleStatusAsync(ApplicationUser user, string purpose)
        {
            var now = DateTimeOffset.UtcNow;
            var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var tomorrowStart = todayStart.AddDays(1);

            var query = _context.OtpSendLogs
                .AsNoTracking()
                .Where(log => log.UserId == user.Id && log.Purpose == purpose);

            var todayCount = await query.CountAsync(log => log.SentAt >= todayStart && log.SentAt < tomorrowStart);
            var lastSentAt = await query
                .OrderByDescending(log => log.SentAt)
                .Select(log => (DateTimeOffset?)log.SentAt)
                .FirstOrDefaultAsync();

            var nextAllowedAt = lastSentAt?.Add(TwilioOtpCooldown);
            var retryAfterSeconds = nextAllowedAt.HasValue && nextAllowedAt.Value > now
                ? Math.Max(0, (int)Math.Ceiling((nextAllowedAt.Value - now).TotalSeconds))
                : 0;

            return new OtpSendThrottleStatus
            {
                DailyRequestLimit = TwilioOtpDailyRequestLimit,
                DailyRequestsRemaining = Math.Max(0, TwilioOtpDailyRequestLimit - todayCount),
                RetryAfterSeconds = retryAfterSeconds,
                NextAllowedAt = retryAfterSeconds > 0 ? nextAllowedAt : null,
                DailyLimitResetsAt = tomorrowStart
            };
        }

        private async Task AssertTwilioOtpSendAllowedAsync(
            ApplicationUser user,
            string purpose,
            string recipient)
        {
            if (string.IsNullOrWhiteSpace(recipient))
            {
                return;
            }

            var status = await GetTwilioOtpThrottleStatusAsync(user, purpose);
            if (status.RetryAfterSeconds > 0)
            {
                throw new OtpSendThrottledException(
                    $"Please wait {status.RetryAfterSeconds} seconds before requesting another OTP for this step.",
                    status);
            }

            if (status.DailyLimitReached)
            {
                throw new OtpSendThrottledException(
                    "Daily OTP request limit reached for this step. Please try again tomorrow.",
                    status);
            }
        }

        private async Task RecordTwilioOtpSendAsync(
            ApplicationUser user,
            string purpose,
            string channel,
            string recipient)
        {
            if (string.IsNullOrWhiteSpace(recipient))
            {
                return;
            }

            _context.OtpSendLogs.Add(new OtpSendLog
            {
                UserId = user.Id,
                Purpose = purpose,
                Channel = channel,
                Recipient = recipient,
                SentAt = DateTimeOffset.UtcNow
            });
            await _context.SaveChangesAsync();
        }

        private async Task RecordTwilioOtpSendsAsync(ApplicationUser user, IReadOnlyCollection<PlannedOtpSend> plannedSends)
        {
            if (plannedSends.Count == 0)
            {
                return;
            }

            foreach (var plannedSend in plannedSends)
            {
                if (string.IsNullOrWhiteSpace(plannedSend.Recipient))
                {
                    continue;
                }

                _context.OtpSendLogs.Add(new OtpSendLog
                {
                    UserId = user.Id,
                    Purpose = plannedSend.Purpose,
                    Channel = plannedSend.Channel,
                    Recipient = plannedSend.Recipient,
                    SentAt = DateTimeOffset.UtcNow
                });
            }

            await _context.SaveChangesAsync();
        }

        private sealed record PlannedOtpSend(
            string Purpose,
            string Channel,
            string Recipient,
            string ValueTokenName,
            string ExpiryTokenName,
            string MessagePrefix);

        private static string BuildInternationalPhoneNumber(string? countryCode, string? phoneNumber)
        {
            var rawPhoneNumber = PhoneNumberHelper.NormalizeOrEmpty(phoneNumber);
            if (string.IsNullOrWhiteSpace(rawPhoneNumber))
            {
                return string.Empty;
            }

            if (rawPhoneNumber.StartsWith("+", StringComparison.Ordinal))
            {
                return rawPhoneNumber;
            }

            var digits = new string(rawPhoneNumber.Where(char.IsDigit).ToArray());
            if (digits.StartsWith("00", StringComparison.Ordinal))
            {
                return $"+{digits[2..]}";
            }

            var countryDigits = new string((countryCode ?? "+237").Where(char.IsDigit).ToArray());
            if (string.IsNullOrWhiteSpace(countryDigits))
            {
                countryDigits = "237";
            }

            return digits.StartsWith(countryDigits, StringComparison.Ordinal)
                ? $"+{digits}"
                : $"+{countryDigits}{digits}";
        }

        private string? BuildVerifyUrl(
            string email,
            bool isVisitor,
            bool useGeneralAccountPage = false,
            string? returnUrl = null)
        {
            var portalBaseUrl = _configuration["Portal:BaseUrl"]?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(portalBaseUrl))
            {
                return null;
            }

            var path = useGeneralAccountPage
                ? "/Auth/VerifyAccount"
                : isVisitor ? "/Auth/VerifyVisitorAccount" : "/Auth/VerifyLandlordEmail";

            var url = $"{portalBaseUrl}{path}?email={Uri.EscapeDataString(email)}";
            if (!string.IsNullOrWhiteSpace(returnUrl))
            {
                url += $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
            }

            return url;
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
