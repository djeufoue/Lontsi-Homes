using Common.CommunicationModels;
using Common.Enums;
using Common.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.Auth;
using RentHub.Portal.ViewModels.Profile;
using System.Security.Claims;
using System.Text.Json;
using System.Globalization;

namespace RentHub.Portal.Controllers
{
    [Authorize]
    public class ProfileController : Controller
    {
        private const string SmsVerificationUnavailableMessage = "Phone verification is not required for this account.";

        private readonly RentHubApiClient _api;
        private readonly PortalAuthSessionService _authSession;
        private readonly IStringLocalizer<SharedResource> _localizer;
        private readonly ILogger<ProfileController> _logger;

        public ProfileController(
            RentHubApiClient api,
            PortalAuthSessionService authSession,
            IStringLocalizer<SharedResource> localizer,
            ILogger<ProfileController> logger)
        {
            _api = api;
            _authSession = authSession;
            _localizer = localizer;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            try
            {
                return View(await BuildProfileIndexVmAsync());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load profile overview in Portal");
                TempData["Error"] = "Unable to load your profile right now. Please try again.";
                return RedirectToAction("Index", "Home");
            }
        }

        [HttpGet]
        public async Task<IActionResult> Payments()
        {
            try
            {
                var model = await BuildProfileIndexVmAsync();
                if (model.Overview.IsSubscriptionExempt)
                {
                    TempData["Info"] = "Your landlord account has a subscription exemption, so no payment plan is required.";
                    return RedirectToAction("Index", "Properties");
                }

                return View(model);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load payment center in Portal");
                TempData["Error"] = "Unable to load your payment center right now. Please try again.";
                return RedirectToAction(nameof(Index));
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateLanguage(UpdateLanguageVm model)
        {
            if (!ModelState.IsValid || !PlatformLanguageOptions.IsSupported(model.Language))
            {
                TempData["Error"] = _localizer["Select a supported language."].Value;
                return RedirectToAction(nameof(Index));
            }

            try
            {
                var response = await _api.PostAsync<UpdatePlatformLanguageRequest, UpdatePlatformLanguageResponse>(
                    "Account/language",
                    new UpdatePlatformLanguageRequest { Language = model.Language });

                if (string.IsNullOrWhiteSpace(response.Token) ||
                    !PlatformLanguageOptions.IsSupported(response.Language))
                {
                    throw new InvalidOperationException("The language update response was incomplete.");
                }

                await _authSession.PersistTokenAsync(response.Token);

                var culture = CultureInfo.GetCultureInfo(response.Language.ToCultureName());
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                TempData["Success"] = _localizer["Your language preference has been updated."].Value;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update the authenticated user's language preference");
                TempData["Error"] = _localizer["Unable to update your language preference right now. Please try again."].Value;
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateEmailLanguage(UpdateEmailLanguageVm model)
        {
            if (!ModelState.IsValid || !PlatformLanguageOptions.IsSupported(model.EmailLanguage))
            {
                TempData["Error"] = _localizer["Select a supported email language."].Value;
                return RedirectToAction(nameof(Index));
            }

            try
            {
                var response = await _api.PostAsync<UpdateEmailLanguageRequest, UpdateEmailLanguageResponse>(
                    "Account/email-language",
                    new UpdateEmailLanguageRequest { EmailLanguage = model.EmailLanguage });

                if (!PlatformLanguageOptions.IsSupported(response.EmailLanguage))
                {
                    throw new InvalidOperationException("The email language update response was incomplete.");
                }

                TempData["Success"] = _localizer["Your email language preference has been updated."].Value;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update the authenticated user's email language preference");
                TempData["Error"] = _localizer["Unable to update your email language preference right now. Please try again."].Value;
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        [Authorize(Roles = "Landlord")]
        public async Task<IActionResult> PayoutSetup(bool refresh = false)
        {
            try
            {
                var overview = await _api.GetAsync<ProfileOverviewDto>("Account/profile-overview");
                if (overview.IsSubscriptionExempt)
                {
                    TempData["Info"] = "Your landlord account has a subscription exemption, so payout setup is not required.";
                    return RedirectToAction("Index", "Properties");
                }

                if (!overview.AutomaticPaymentsEnabled)
                {
                    TempData["Info"] = "Stripe setup is skipped while automatic payments are disabled.";
                    return RedirectToAction(nameof(Payments));
                }

                if (refresh)
                {
                    return await RedirectToStripePayoutSetupAsync();
                }

                var status = await _api.GetAsync<StripePayoutAccountStatusDto>("PayoutAccounts/stripe/status");
                return View(status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load Stripe payout setup in Portal");
                TempData["Error"] = SafeUserMessage(
                    ParseApiMessage(ex.Message),
                    "Unable to load payout setup right now. Please try again.");
                return RedirectToAction(nameof(Payments));
            }
        }

        [HttpPost]
        [Authorize(Roles = "Landlord")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StartPayoutSetup()
        {
            try
            {
                if (await HasSubscriptionExemptionAsync())
                {
                    return RedirectExemptLandlordToProperties();
                }

                return await RedirectToStripePayoutSetupAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start Stripe payout setup in Portal");
                TempData["Error"] = SafeUserMessage(
                    ParseApiMessage(ex.Message),
                    "Unable to start payout setup right now. Please try again.");
            }

            return RedirectToAction(nameof(PayoutSetup));
        }

        [HttpGet]
        [Authorize(Roles = "Landlord")]
        public async Task<IActionResult> PayoutSetupReturn()
        {
            try
            {
                if (await HasSubscriptionExemptionAsync())
                {
                    return RedirectExemptLandlordToProperties();
                }

                var status = await _api.GetAsync<StripePayoutAccountStatusDto>("PayoutAccounts/stripe/status");
                if (status.SetupComplete)
                {
                    TempData["Success"] = "Payout account setup is complete. You can now choose a subscription.";
                    return RedirectToAction(nameof(Payments));
                }

                TempData["Info"] = "Payout account setup was saved, but Stripe still needs a few details before payouts are enabled. Use Continue Stripe setup to finish the remaining requirements.";
                return RedirectToAction(nameof(PayoutSetup));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh Stripe payout setup return in Portal");
                TempData["Error"] = SafeUserMessage(
                    ParseApiMessage(ex.Message),
                    "Unable to verify payout setup right now. Please refresh in a moment.");
                return RedirectToAction(nameof(Payments));
            }
        }

        private async Task<IActionResult> RedirectToStripePayoutSetupAsync()
        {
            var status = await _api.GetAsync<StripePayoutAccountStatusDto>("PayoutAccounts/stripe/status");
            if (!status.IsKycApproved)
            {
                TempData["Error"] = "Your KYC must be approved by an administrator before you can set up a Stripe payout account.";
                return RedirectToAction(nameof(PayoutSetup));
            }

            if (!status.IsPlatformReady)
            {
                TempData["Error"] = SafeUserMessage(
                    status.PlatformReadinessMessage,
                    "Stripe Connect setup is temporarily unavailable. Please continue testing the rest of the platform for now.");
                return RedirectToAction(nameof(PayoutSetup));
            }

            var link = await _api.PostAsync<object, StripePayoutSetupLinkDto>(
                "PayoutAccounts/stripe/start",
                new { });

            if (!string.IsNullOrWhiteSpace(link.OnboardingUrl))
            {
                return Redirect(link.OnboardingUrl);
            }

            TempData["Error"] = "Stripe did not return an onboarding link. Please try again.";
            return RedirectToAction(nameof(PayoutSetup));
        }

        [HttpGet]
        [Authorize(Roles = "Landlord")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> MobileMoneySetup()
        {
            try
            {
                var overview = await _api.GetAsync<ProfileOverviewDto>("Account/profile-overview");
                return Json(new
                {
                    overview.Email,
                    PrimaryPhoneNumber = overview.PhoneNumber,
                    overview.UsePrimaryPhoneForSubscriptionPayments,
                    overview.SubscriptionPaymentPhoneNumber,
                    SubscriptionPaymentChannel = overview.SubscriptionPaymentChannel?.ToString(),
                    overview.IsSubscriptionPaymentPhoneVerified,
                    overview.UsePrimaryPhoneForRentPayouts,
                    overview.PayoutPhoneNumber,
                    PayoutChannel = overview.PayoutChannel?.ToString(),
                    overview.IsPayoutPhoneVerified,
                    overview.WhatsAppPhoneNumber,
                    overview.IsWhatsAppPhoneVerified,
                    overview.SmsVerificationEnabled
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load Mobile Money setup in Portal");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    Message = "Unable to reload Mobile Money setup right now. Please try again."
                });
            }
        }

        [HttpPost]
        [Authorize(Roles = "Landlord")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StartMobileMoneyUpdate(string target, string phoneNumber, PayoutChannelEnum? channel)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return BadRequest(new { Message = "Choose the phone number you want to update." });
            }

            try
            {
                var overview = await _api.GetAsync<ProfileOverviewDto>("Account/profile-overview");
                if (!overview.SmsVerificationEnabled)
                {
                    return BadRequest(new { Message = SmsVerificationUnavailableMessage });
                }

                if (string.IsNullOrWhiteSpace(phoneNumber))
                {
                    return BadRequest(new { Message = "Enter the new phone number before requesting an OTP." });
                }

                var response = await _api.PostAsync<StartMobilePaymentNumberUpdateRequest, JsonElement>(
                    "Account/mobile-payments/start-update",
                    new StartMobilePaymentNumberUpdateRequest
                    {
                        Target = target,
                        PhoneNumber = phoneNumber.Trim(),
                        Channel = channel
                    });

                return ApiJson(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mobile Money single-number update failed for target {Target}", target);
                return ApiError(ex, "Unable to send the OTP right now. Please try again.");
            }
        }

        [HttpPost]
        [Authorize(Roles = "Landlord")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelMobileMoneyUpdate(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return BadRequest(new { Message = "Choose the phone number update to discard." });
            }

            try
            {
                var response = await _api.PostAsync<CancelMobilePaymentNumberUpdateRequest, JsonElement>(
                    "Account/mobile-payments/cancel-update",
                    new CancelMobilePaymentNumberUpdateRequest
                    {
                        Target = target
                    });

                return ApiJson(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mobile Money update cancel failed for target {Target}", target);
                return ApiError(ex, "Unable to discard the Mobile Money update right now. Please try again.");
            }
        }

        [HttpPost]
        [Authorize(Roles = "Landlord")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateMobileMoney(LandlordMobilePaymentsVm vm)
        {
            var email = ResolveAuthenticatedEmail();
            if (string.IsNullOrWhiteSpace(email))
            {
                return Unauthorized(new { Message = "Your session expired. Please sign in again." });
            }

            vm.Email = email;
            ModelState.Remove(nameof(LandlordMobilePaymentsVm.Email));

            if (!ModelState.IsValid)
            {
                return BadRequest(new { Message = ReadFirstModelError("Check the Mobile Money details and try again.") });
            }

            try
            {
                var overview = await _api.GetAsync<ProfileOverviewDto>("Account/profile-overview");
                if (!overview.SmsVerificationEnabled)
                {
                    return BadRequest(new { Message = SmsVerificationUnavailableMessage });
                }

                if (HasPendingMobilePaymentVerification(overview))
                {
                    return BadRequest(new { Message = "Complete the pending OTP validation before changing Mobile Money setup again." });
                }

                var request = new UpsertLandlordMobilePaymentsRequest
                {
                    Email = email,
                    UsePrimaryPhoneForSubscriptionPayments = vm.UsePrimaryPhoneForSubscriptionPayments,
                    SubscriptionPaymentPhoneNumber = vm.SubscriptionPaymentPhoneNumber,
                    SubscriptionPaymentChannel = vm.SubscriptionPaymentChannel,
                    UsePrimaryPhoneForRentPayouts = vm.UsePrimaryPhoneForRentPayouts,
                    PayoutPhoneNumber = vm.PayoutPhoneNumber,
                    PayoutChannel = vm.PayoutChannel,
                    WhatsAppPhoneNumber = vm.WhatsAppPhoneNumber
                };

                var response = await _api.PostAsync<UpsertLandlordMobilePaymentsRequest, JsonElement>(
                    "Account/landlord-registration/mobile-payments",
                    request);

                return ApiJson(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mobile Money update failed from profile for {Email}", email);
                return ApiError(ex, "Unable to save Mobile Money setup right now. Please try again.");
            }
        }

        [HttpPost]
        [Authorize(Roles = "Landlord")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyMobileMoneyOtp(string target, string otp)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return BadRequest(new { Message = "Choose the number you want to verify." });
            }

            try
            {
                var overview = await _api.GetAsync<ProfileOverviewDto>("Account/profile-overview");
                if (!overview.SmsVerificationEnabled)
                {
                    return BadRequest(new { Message = SmsVerificationUnavailableMessage });
                }

                if (string.IsNullOrWhiteSpace(otp))
                {
                    return BadRequest(new { Message = "Enter the OTP code before verifying this number." });
                }

                var response = await _api.PostAsync<VerifyMobilePaymentOtpRequest, JsonElement>(
                    "Account/mobile-payments/verify-otp",
                    new VerifyMobilePaymentOtpRequest
                    {
                        Target = target,
                        Otp = otp
                    });

                return ApiJson(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mobile Money OTP verification failed for target {Target}", target);
                return ApiError(ex, "Unable to verify the OTP right now. Please try again.");
            }
        }

        [HttpPost]
        [Authorize(Roles = "Landlord")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResendMobileMoneyOtp(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return BadRequest(new { Message = "Choose the number that should receive a new OTP." });
            }

            try
            {
                var overview = await _api.GetAsync<ProfileOverviewDto>("Account/profile-overview");
                if (!overview.SmsVerificationEnabled)
                {
                    return BadRequest(new { Message = SmsVerificationUnavailableMessage });
                }

                var response = await _api.PostAsync<ResendMobilePaymentOtpRequest, JsonElement>(
                    "Account/mobile-payments/resend-otp",
                    new ResendMobilePaymentOtpRequest
                    {
                        Target = target
                    });

                return ApiJson(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mobile Money OTP resend failed for target {Target}", target);
                return ApiError(ex, "Unable to resend the OTP right now. Please try again.");
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Upgrade(int planId)
        {
            if (planId <= 0)
            {
                TempData["Error"] = "Please choose a valid subscription plan.";
                return RedirectToAction(nameof(Payments));
            }

            try
            {
                if (await HasSubscriptionExemptionAsync())
                {
                    return RedirectExemptLandlordToProperties();
                }

                var response = await _api.PostAsync<object, JsonElement>($"Subscriptions/subscribe/{planId}", new { });
                var message = ReadMessage(response);

                TempData["Success"] = SafeUserMessage(
                    message,
                    "Subscription updated successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Subscription upgrade failed for plan {PlanId}", planId);

                var apiMessage = ParseApiMessage(ex.Message);
                TempData["Error"] = SafeUserMessage(
                    apiMessage,
                    "Unable to update your subscription right now. Please try again.");
            }

            return RedirectToAction(nameof(Payments));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StartCheckout(
            int planId,
            PaymentMethodEnum paymentMethod = PaymentMethodEnum.Card,
            bool allowAutomaticCardPayments = true)
        {
            if (planId <= 0)
            {
                TempData["Error"] = "Please choose a valid subscription plan.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                if (await HasSubscriptionExemptionAsync())
                {
                    return RedirectExemptLandlordToProperties();
                }

                var session = await _api.PostAsync<StartSubscriptionCheckoutRequest, SubscriptionCheckoutSessionDto>(
                    $"Subscriptions/checkout/{planId}",
                    new StartSubscriptionCheckoutRequest
                    {
                        PaymentMethod = paymentMethod,
                        AllowAutomaticCardPayments = allowAutomaticCardPayments
                    });

                if (!string.IsNullOrWhiteSpace(session.AuthorizationUrl))
                {
                    return Redirect(session.AuthorizationUrl);
                }

                TempData["Success"] = string.IsNullOrWhiteSpace(session.PaymentInstructions)
                    ? "Payment request sent. Confirm it on your phone to activate your subscription."
                    : session.PaymentInstructions;
                return RedirectToAction(nameof(Payments));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Subscription checkout failed for plan {PlanId}", planId);

                var apiMessage = ParseApiMessage(ex.Message);
                TempData["Error"] = SafeUserMessage(
                    apiMessage,
                    "Unable to initialize the subscription payment right now. Please try again.");

                return RedirectToAction(nameof(Payments));
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestManualSubscription(int planId, int durationMonths = 12)
        {
            if (planId <= 0)
            {
                TempData["Error"] = "Please choose a valid subscription plan.";
                return RedirectToAction(nameof(Payments));
            }

            if (durationMonths is < 6 or > 36)
            {
                TempData["Error"] = "Choose a subscription duration between 6 and 36 months.";
                return RedirectToAction(nameof(Payments));
            }

            try
            {
                if (await HasSubscriptionExemptionAsync())
                {
                    return RedirectExemptLandlordToProperties();
                }

                await _api.PostAsync($"Subscriptions/manual-request/{planId}", new ManualSubscriptionActivationRequest
                {
                    DurationMonths = durationMonths
                });
                TempData["Success"] = "Your activation request was sent. The administrator will review it after receiving your cash payment.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual subscription request failed for plan {PlanId}", planId);
                TempData["Error"] = SafeUserMessage(
                    ParseApiMessage(ex.Message),
                    "Unable to submit the manual subscription request right now.");
            }

            return RedirectToAction(nameof(Payments));
        }

        [HttpGet]
        public async Task<IActionResult> CardCheckout(string? reference = null)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                TempData["Error"] = "Card checkout reference is missing. Please choose a subscription plan again.";
                return RedirectToAction(nameof(Payments));
            }

            try
            {
                if (await HasSubscriptionExemptionAsync())
                {
                    return RedirectExemptLandlordToProperties();
                }

                var session = await _api.GetAsync<SubscriptionCheckoutSessionDto>(
                    $"Subscriptions/checkout-session/{Uri.EscapeDataString(reference)}");

                if (string.IsNullOrWhiteSpace(session.PublishableKey) ||
                    string.IsNullOrWhiteSpace(session.ClientSecret))
                {
                    TempData["Error"] = "Card checkout is not configured yet. Please contact support or try again later.";
                    return RedirectToAction(nameof(Payments));
                }

                return View(new SubscriptionCardCheckoutVm
                {
                    PlanId = session.PlanId,
                    PlanName = session.PlanName,
                    Amount = session.Amount,
                    Currency = session.Currency,
                    PaymentReference = session.PaymentReference,
                    ProviderReference = session.ProviderReference,
                    PublishableKey = session.PublishableKey,
                    ClientSecret = session.ClientSecret,
                    ReturnUrl = session.ReturnUrl
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load card checkout for reference {Reference}", reference);
                TempData["Error"] = SafeUserMessage(
                    ParseApiMessage(ex.Message),
                    "Unable to load the secure card form right now. Please try again.");
                return RedirectToAction(nameof(Payments));
            }
        }

        [HttpGet]
        public async Task<IActionResult> SubscriptionCallback(string? reference = null, string? session_id = null)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                TempData["Error"] = "Subscription payment reference is missing.";
                return RedirectToAction(nameof(Payments));
            }

            try
            {
                if (await HasSubscriptionExemptionAsync())
                {
                    return RedirectExemptLandlordToProperties();
                }

                var status = await _api.GetAsync<SubscriptionCheckoutStatusDto>($"Subscriptions/checkout-status/{Uri.EscapeDataString(reference)}");
                TempData[status.PaymentCompleted ? "Success" : "Error"] = string.IsNullOrWhiteSpace(status.Message)
                    ? (status.PaymentCompleted ? "Subscription activated successfully." : "The card payment was not completed. Please try again.")
                    : status.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Subscription callback lookup failed for reference {Reference}", reference);
                TempData["Error"] = SafeUserMessage(
                    ParseApiMessage(ex.Message),
                    "Unable to verify the subscription payment right now. Please refresh your profile in a moment.");
            }

            return RedirectToAction(nameof(Payments));
        }

        [HttpGet]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> SubscriptionDetails(string? reference = null)
        {
            try
            {
                if (await HasSubscriptionExemptionAsync())
                {
                    return StatusCode(StatusCodes.Status403Forbidden);
                }

                if (!string.IsNullOrWhiteSpace(reference))
                {
                    try
                    {
                        await _api.GetAsync<SubscriptionCheckoutStatusDto>(
                            $"Subscriptions/checkout-status/{Uri.EscapeDataString(reference)}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Unable to refresh subscription payment status for reference {Reference}", reference);
                    }
                }

                var overview = await _api.GetAsync<ProfileOverviewDto>("Account/profile-overview");
                return PartialView("_SubscriptionDetails", new ProfileIndexVm
                {
                    Overview = overview,
                    NowUtc = DateTimeOffset.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh subscription details");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Unable to refresh subscription details right now.");
            }
        }

        private async Task<bool> HasSubscriptionExemptionAsync()
        {
            var overview = await _api.GetAsync<ProfileOverviewDto>("Account/profile-overview");
            return overview.IsSubscriptionExempt;
        }

        private IActionResult RedirectExemptLandlordToProperties()
        {
            TempData["Info"] = "Your landlord account has a subscription exemption, so no payment plan is required.";
            return RedirectToAction("Index", "Properties");
        }

        private static string? ReadMessage(JsonElement element)
        {
            if (TryGetPropertyIgnoreCase(element, "message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String)
            {
                return messageElement.GetString();
            }

            return null;
        }

        private async Task<ProfileIndexVm> BuildProfileIndexVmAsync()
        {
            var overview = await _api.GetAsync<ProfileOverviewDto>("Account/profile-overview");
            return new ProfileIndexVm
            {
                Overview = overview,
                NowUtc = DateTimeOffset.UtcNow
            };
        }

        private ContentResult ApiJson(JsonElement element)
        {
            return Content(element.GetRawText(), "application/json");
        }

        private IActionResult ApiError(Exception ex, string fallback)
        {
            var payload = ParseApiErrorPayload(ex.Message);
            payload.Message = SafeUserMessage(payload.Message, fallback);

            var statusCode = payload.Code switch
            {
                "AUTH_SESSION_EXPIRED" => StatusCodes.Status401Unauthorized,
                "OTP_REQUEST_LIMITED" => StatusCodes.Status429TooManyRequests,
                "SERVER_ERROR" => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status400BadRequest
            };

            return StatusCode(statusCode, payload);
        }

        private string? ResolveAuthenticatedEmail()
        {
            return User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name;
        }

        private string ReadFirstModelError(string fallback)
        {
            return ModelState.Values
                .SelectMany(value => value.Errors)
                .Select(error => error.ErrorMessage)
                .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message))
                ?? fallback;
        }

        private static bool HasPendingMobilePaymentVerification(ProfileOverviewDto overview)
        {
            if (!overview.SmsVerificationEnabled)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(overview.SubscriptionPaymentPhoneNumber) && !overview.IsSubscriptionPaymentPhoneVerified ||
                   !string.IsNullOrWhiteSpace(overview.PayoutPhoneNumber) && !overview.IsPayoutPhoneVerified ||
                   !string.IsNullOrWhiteSpace(overview.WhatsAppPhoneNumber) && !overview.IsWhatsAppPhoneVerified;
        }

        private static UpsertLandlordMobilePaymentsRequest BuildMobileMoneyRequestFromOverview(
            ProfileOverviewDto overview,
            string email)
        {
            var hasPrimaryPhone = !string.IsNullOrWhiteSpace(overview.PhoneNumber);
            return new UpsertLandlordMobilePaymentsRequest
            {
                Email = email,
                UsePrimaryPhoneForSubscriptionPayments = overview.UsePrimaryPhoneForSubscriptionPayments && hasPrimaryPhone,
                SubscriptionPaymentPhoneNumber = overview.SubscriptionPaymentPhoneNumber ?? overview.PhoneNumber,
                SubscriptionPaymentChannel = overview.SubscriptionPaymentChannel ?? PayoutChannelEnum.MtnMoney,
                UsePrimaryPhoneForRentPayouts = overview.UsePrimaryPhoneForRentPayouts && hasPrimaryPhone,
                PayoutPhoneNumber = overview.PayoutPhoneNumber ?? overview.PhoneNumber,
                PayoutChannel = overview.PayoutChannel ?? PayoutChannelEnum.MtnMoney,
                WhatsAppPhoneNumber = overview.WhatsAppPhoneNumber
            };
        }

        private static bool SamePhone(string? left, string? right)
        {
            var normalizedLeft = NormalizePhone(left);
            var normalizedRight = NormalizePhone(right);
            return normalizedLeft.Length > 0 &&
                   normalizedRight.Length > 0 &&
                   string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
        }

        private static string NormalizePhone(string? phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
                return string.Empty;

            if (CameroonMobileMoneyNumberHelper.TryNormalizeNationalNumber(phoneNumber, out var normalized))
                return normalized;

            var digits = new string(phoneNumber.Where(char.IsDigit).ToArray());
            return digits.StartsWith("00", StringComparison.Ordinal) ? digits[2..] : digits;
        }

        private static ApiErrorPayload ParseApiErrorPayload(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new ApiErrorPayload();

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.String)
                {
                    return new ApiErrorPayload { Message = root.GetString() };
                }

                return new ApiErrorPayload
                {
                    Code = ReadString(root, "code"),
                    Message = ReadString(root, "message"),
                    RetryAfterSeconds = ReadInt(root, "retryAfterSeconds"),
                    DailyRequestLimit = ReadInt(root, "dailyRequestLimit"),
                    DailyRequestsRemaining = ReadInt(root, "dailyRequestsRemaining"),
                    DailyLimitReached = ReadBool(root, "dailyLimitReached")
                };
            }
            catch
            {
                return new ApiErrorPayload { Message = raw };
            }
        }

        private static string? ReadString(JsonElement element, string propertyName)
        {
            return TryGetPropertyIgnoreCase(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static int? ReadInt(JsonElement element, string propertyName)
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out var number) => number,
                JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
                _ => null
            };
        }

        private static bool? ReadBool(JsonElement element, string propertyName)
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
                _ => null
            };
        }

        private static string? ParseApiMessage(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.String)
                    return root.GetString();

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetPropertyIgnoreCase(root, "message", out var messageElement) &&
                        messageElement.ValueKind == JsonValueKind.String)
                    {
                        return messageElement.GetString();
                    }
                }
            }
            catch
            {
                // Fallback to raw message below.
            }

            return raw;
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static string SafeUserMessage(string? apiMessage, string fallback)
        {
            if (string.IsNullOrWhiteSpace(apiMessage))
                return fallback;

            return LooksTechnicalMessage(apiMessage) ? fallback : apiMessage;
        }

        private static bool LooksTechnicalMessage(string message)
        {
            var normalized = message.Trim();
            if (normalized.Length == 0)
                return true;

            var technicalFragments = new[]
            {
                "exception",
                "stack trace",
                "inner exception",
                "dbupdateexception",
                "sqlexception",
                "invalid column name",
                "entityframework",
                " at "
            };

            return technicalFragments.Any(fragment =>
                normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }

        private sealed class ApiErrorPayload
        {
            public string? Code { get; set; }
            public string? Message { get; set; }
            public int? RetryAfterSeconds { get; set; }
            public int? DailyRequestLimit { get; set; }
            public int? DailyRequestsRemaining { get; set; }
            public bool? DailyLimitReached { get; set; }
        }
    }
}
