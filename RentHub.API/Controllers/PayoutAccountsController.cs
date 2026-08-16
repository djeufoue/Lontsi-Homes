using Common.CommunicationModels;
using Common.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Helpers;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Payments;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = "Landlord")]
    public class PayoutAccountsController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApplicationDbContext _context;
        private readonly IStripeConnectService _stripeConnectService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<PayoutAccountsController> _logger;

        public PayoutAccountsController(
            UserManager<ApplicationUser> userManager,
            ApplicationDbContext context,
            IStripeConnectService stripeConnectService,
            IConfiguration configuration,
            ILogger<PayoutAccountsController> logger)
        {
            _userManager = userManager;
            _context = context;
            _stripeConnectService = stripeConnectService;
            _configuration = configuration;
            _logger = logger;
        }

        [HttpGet("stripe/status")]
        public async Task<IActionResult> GetStripeStatus()
        {
            try
            {
                if (!await PaymentAvailabilityHelper.IsPlatformAutomaticPaymentEnabledAsync(_context))
                {
                    return StatusCode(StatusCodes.Status409Conflict, new
                    {
                        Code = "AUTOMATIC_PAYMENTS_DISABLED",
                        Message = "Stripe payout setup is hidden while automatic payments are disabled."
                    });
                }

                var user = await GetCurrentUserAsync();
                if (user == null)
                {
                    return Unauthorized();
                }

                var kycStatus = await GetKycStatusAsync(user.Id);
                if (_stripeConnectService.IsConnectPlatformEnabled() &&
                    !string.IsNullOrWhiteSpace(user.StripeConnectAccountId))
                {
                    var status = await _stripeConnectService.RetrieveAccountAsync(user.StripeConnectAccountId);
                    if (status != null)
                    {
                        await ApplyStripeStatusAsync(user, status);
                    }
                }

                return Ok(BuildStatusDto(user, kycStatus));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load Stripe payout status");
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    Code = "STRIPE_PAYOUT_STATUS_FAILED",
                    Message = "Unable to load payout account status right now. Please try again."
                });
            }
        }

        [HttpPost("stripe/start")]
        public async Task<IActionResult> StartStripeSetup()
        {
            try
            {
                if (!await PaymentAvailabilityHelper.IsPlatformAutomaticPaymentEnabledAsync(_context))
                {
                    return StatusCode(StatusCodes.Status409Conflict, new
                    {
                        Code = "AUTOMATIC_PAYMENTS_DISABLED",
                        Message = "Stripe payout setup is unavailable while automatic payments are disabled."
                    });
                }

                var user = await GetCurrentUserAsync();
                if (user == null)
                {
                    return Unauthorized();
                }

                var kycStatus = await GetKycStatusAsync(user.Id);
                if (kycStatus != LandlordKycStatusEnum.Approved)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        Code = "KYC_APPROVAL_REQUIRED",
                        Message = "Your KYC must be approved by an administrator before you can set up a Stripe payout account."
                    });
                }

                if (!_stripeConnectService.IsConnectPlatformEnabled())
                {
                    return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                    {
                        Code = "STRIPE_CONNECT_PLATFORM_NOT_READY",
                        Message = _stripeConnectService.GetPlatformNotReadyMessage()
                    });
                }

                StripeConnectAccountStatus? status = null;
                if (string.IsNullOrWhiteSpace(user.StripeConnectAccountId))
                {
                    status = await _stripeConnectService.CreateExpressAccountAsync(user);
                    await ApplyStripeStatusAsync(user, status);
                }
                else
                {
                    status = await _stripeConnectService.RetrieveAccountAsync(user.StripeConnectAccountId);
                    if (status != null)
                    {
                        await ApplyStripeStatusAsync(user, status);
                    }
                }

                if (string.IsNullOrWhiteSpace(user.StripeConnectAccountId))
                {
                    return StatusCode(StatusCodes.Status502BadGateway, new
                    {
                        Code = "STRIPE_PAYOUT_ACCOUNT_FAILED",
                        Message = "Stripe did not return a payout account. Please try again."
                    });
                }

                var portalBaseUrl = _configuration["Portal:BaseUrl"]?.Trim().TrimEnd('/');
                if (string.IsNullOrWhiteSpace(portalBaseUrl))
                {
                    return StatusCode(StatusCodes.Status500InternalServerError, new
                    {
                        Code = "PORTAL_BASE_URL_MISSING",
                        Message = "Portal base URL is missing. Please configure Portal:BaseUrl before starting payout setup."
                    });
                }

                var returnUrl = $"{portalBaseUrl}/Profile/PayoutSetupReturn";
                var refreshUrl = $"{portalBaseUrl}/Profile/PayoutSetup?refresh=true";
                var onboardingUrl = await _stripeConnectService.CreateOnboardingLinkAsync(
                    user.StripeConnectAccountId,
                    returnUrl,
                    refreshUrl);

                return Ok(new StripePayoutSetupLinkDto
                {
                    AccountId = user.StripeConnectAccountId,
                    OnboardingUrl = onboardingUrl,
                    Status = BuildStatusDto(user, kycStatus)
                });
            }
            catch (StripeConnectCountryUnsupportedException ex)
            {
                _logger.LogInformation(ex, "Stripe payout setup is not available for this landlord country");
                return BadRequest(new
                {
                    Code = "STRIPE_CONNECT_COUNTRY_UNSUPPORTED",
                    Message = ex.Message
                });
            }
            catch (StripeConnectPlatformNotReadyException ex)
            {
                _logger.LogWarning(ex, "Stripe Connect platform setup is not ready");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    Code = "STRIPE_CONNECT_PLATFORM_NOT_READY",
                    Message = ex.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start Stripe payout setup");
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    Code = "STRIPE_PAYOUT_SETUP_FAILED",
                    Message = "Unable to start payout setup right now. Please try again."
                });
            }
        }

        private async Task<ApplicationUser?> GetCurrentUserAsync()
        {
            var userId = UserHelpers.GetUserId(User);
            return string.IsNullOrWhiteSpace(userId)
                ? null
                : await _userManager.FindByIdAsync(userId);
        }

        private async Task<LandlordKycStatusEnum> GetKycStatusAsync(string userId)
        {
            return await _context.LandlordKycProfiles
                .AsNoTracking()
                .Where(profile => profile.UserId == userId)
                .Select(profile => (LandlordKycStatusEnum?)profile.Status)
                .FirstOrDefaultAsync() ?? LandlordKycStatusEnum.NotStarted;
        }

        private async Task ApplyStripeStatusAsync(ApplicationUser user, StripeConnectAccountStatus status)
        {
            var wasComplete = IsStripePayoutSetupComplete(user);
            var isComplete = !string.IsNullOrWhiteSpace(status.AccountId) &&
                             status.DetailsSubmitted &&
                             status.ChargesEnabled &&
                             status.PayoutsEnabled;

            user.StripeConnectAccountId = string.IsNullOrWhiteSpace(status.AccountId)
                ? user.StripeConnectAccountId
                : status.AccountId;
            user.StripePayoutDetailsSubmitted = status.DetailsSubmitted;
            user.StripeChargesEnabled = status.ChargesEnabled;
            user.StripePayoutsEnabled = status.PayoutsEnabled;
            user.StripePayoutDisabledReason = status.DisabledReason;
            user.StripePayoutRequirementsSummary = status.RequirementsSummary;
            user.StripePayoutSetupStartedAt ??= DateTimeOffset.UtcNow;
            user.StripePayoutStatusUpdatedAt = DateTimeOffset.UtcNow;

            if (isComplete)
            {
                user.StripePayoutSetupCompletedAt ??= DateTimeOffset.UtcNow;
            }
            else if (wasComplete)
            {
                user.StripePayoutSetupCompletedAt = null;
            }

            var result = await _userManager.UpdateAsync(user);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException("Unable to save payout account status.");
            }
        }

        private StripePayoutAccountStatusDto BuildStatusDto(ApplicationUser user, LandlordKycStatusEnum kycStatus)
        {
            var hasAccount = !string.IsNullOrWhiteSpace(user.StripeConnectAccountId);
            var setupComplete = IsStripePayoutSetupComplete(user);
            var isPlatformReady = _stripeConnectService.IsConnectPlatformEnabled();
            var platformReadinessMessage = isPlatformReady
                ? string.Empty
                : _stripeConnectService.GetPlatformNotReadyMessage();
            var countryIsoCode = _stripeConnectService.ResolveConnectCountry(user);
            var isCountrySupported = _stripeConnectService.IsConnectCountrySupported(countryIsoCode);
            var unsupportedMessage = isCountrySupported
                ? string.Empty
                : _stripeConnectService.GetUnsupportedCountryMessage(countryIsoCode);
            var message = kycStatus != LandlordKycStatusEnum.Approved
                ? "Your KYC must be approved by an administrator before Stripe payout setup becomes available."
                : !isPlatformReady
                ? platformReadinessMessage
                : !isCountrySupported
                ? unsupportedMessage
                : setupComplete
                    ? "Payout account is ready. Tenant rent payments can be routed to you after rent payment is enabled."
                    : hasAccount
                        ? "Stripe still needs a few details before payouts are enabled."
                        : "Set up a payout account before subscribing so tenant rent payments can be routed to you later.";

            return new StripePayoutAccountStatusDto
            {
                KycStatus = kycStatus,
                IsPlatformReady = isPlatformReady,
                PlatformReadinessMessage = platformReadinessMessage,
                AccountId = user.StripeConnectAccountId ?? string.Empty,
                CountryIsoCode = countryIsoCode,
                IsCountrySupported = isCountrySupported,
                CountryUnsupportedMessage = unsupportedMessage,
                HasAccount = hasAccount,
                DetailsSubmitted = user.StripePayoutDetailsSubmitted,
                ChargesEnabled = user.StripeChargesEnabled,
                PayoutsEnabled = user.StripePayoutsEnabled,
                DisabledReason = user.StripePayoutDisabledReason ?? string.Empty,
                RequirementsSummary = user.StripePayoutRequirementsSummary ?? string.Empty,
                SetupStartedAt = user.StripePayoutSetupStartedAt,
                SetupCompletedAt = user.StripePayoutSetupCompletedAt,
                StatusUpdatedAt = user.StripePayoutStatusUpdatedAt,
                Message = message
            };
        }

        private static bool IsStripePayoutSetupComplete(ApplicationUser user)
        {
            return !string.IsNullOrWhiteSpace(user.StripeConnectAccountId) &&
                   user.StripePayoutDetailsSubmitted &&
                   user.StripeChargesEnabled &&
                   user.StripePayoutsEnabled;
        }
    }
}
