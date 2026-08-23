using Common.CommunicationModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.Auth;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Common.Enums;

namespace RentHub.Portal.Controllers
{
    public class AuthController : Controller
    {
        private readonly RentHubApiClient _api;
        private readonly PortalAuthSessionService _authSession;
        private readonly ILogger<AuthController> _logger;

        public AuthController(RentHubApiClient api, PortalAuthSessionService authSession, ILogger<AuthController> logger)
        {
            _api = api;
            _authSession = authSession;
            _logger = logger;
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult Login(string? returnUrl = null, string? email = null)
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToLocal(returnUrl);

            if (TempData["AuthInfo"] is string info)
                ViewBag.AuthInfo = info;

            if (TempData["AuthError"] is string error)
                ViewBag.AuthError = error;

            return View(new LoginVm { ReturnUrl = returnUrl, Email = email?.Trim() ?? string.Empty });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginVm vm)
        {
            if (!ModelState.IsValid) return View(vm);

            try
            {
                var token = await LoginToApi(vm.Email, vm.Password);
                await _authSession.PersistTokenAsync(token);

                return RedirectToLocal(vm.ReturnUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Login failed in Portal for {Email}", vm.Email);

                var apiError = ParseApiError(ex.Message);
                if (string.Equals(apiError.Code, "LANDLORD_ONBOARDING_INCOMPLETE", StringComparison.OrdinalIgnoreCase))
                {
                    TempData["AuthInfo"] = SafeUserMessage(
                        apiError.Message,
                        "Your landlord registration is not complete yet. Continue from the saved step.");

                    return RedirectToOnboardingStep(apiError.NextStep, apiError.Email ?? vm.Email, vm.ReturnUrl);
                }

                if (string.Equals(apiError.Code, "EMAIL_NOT_CONFIRMED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(apiError.Code, "ACCOUNT_VERIFICATION_PENDING", StringComparison.OrdinalIgnoreCase))
                {
                    TempData["AuthInfo"] = SafeUserMessage(
                        apiError.Message,
                        "Your account is not activated yet. Enter the OTP code sent to your email.");

                    return RedirectToAction(nameof(VerifyAccount), new { email = apiError.Email ?? vm.Email, returnUrl = vm.ReturnUrl });
                }

                if (string.Equals(apiError.Code, "VISITOR_ACCOUNT_VERIFICATION_PENDING", StringComparison.OrdinalIgnoreCase))
                {
                    TempData["AuthInfo"] = SafeUserMessage(
                        apiError.Message,
                        "Your visitor account is not activated yet. Enter the OTP codes sent to your email, phone number, and WhatsApp.");

                    return RedirectToAction(nameof(VerifyVisitorAccount), new { email = apiError.Email ?? vm.Email, returnUrl = vm.ReturnUrl });
                }

                ModelState.AddModelError(
                    string.Empty,
                    SafeUserMessage(apiError.Message, "Unable to sign in right now. Please try again."));

                return View(vm);
            }
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> Register(string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToLocal(returnUrl);

            ViewBag.Plans = await GetPlansAsync();
            return View(new RegisterVm { ReturnUrl = returnUrl, CountryCode = "+1" });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(RegisterVm vm)
        {
            if (!ModelState.IsValid)
            {
                ViewBag.Plans = await GetPlansAsync();
                return View(vm);
            }

            try
            {
                var result = await RegisterToApi(vm);

                if (result.RequiresActivation || string.IsNullOrWhiteSpace(result.Token))
                {
                    TempData["AuthInfo"] = string.IsNullOrWhiteSpace(result.Message)
                        ? "Account created. Check your email for OTP and activate your account."
                        : result.Message;

                    return RedirectToOnboardingStep(result.NextStep, result.Email ?? vm.Email, vm.ReturnUrl);
                }

                await _authSession.PersistTokenAsync(result.Token);

                return RedirectToLocal(vm.ReturnUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Register failed in Portal for {Email}", vm.Email);

                var apiError = ParseApiError(ex.Message);
                ModelState.AddModelError(
                    string.Empty,
                    SafeUserMessage(apiError.Message, "Unable to create your account right now. Please try again in a few minutes."));

                ViewBag.Plans = await GetPlansAsync();
                return View(vm);
            }
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> VerifyLandlordEmail(string? email = null, string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToLocal(returnUrl);

            if (TempData["AuthInfo"] is string info)
                ViewBag.AuthInfo = info;

            if (TempData["AuthError"] is string error)
                ViewBag.AuthError = error;

            var status = await TryGetOnboardingStatusAsync(email);
            if (status?.IsComplete == true)
                return RedirectToAction(nameof(Login), new { returnUrl });

            return View(new VerifyLandlordEmailVm
            {
                Email = email ?? status?.Email ?? string.Empty,
                ReturnUrl = returnUrl,
                Status = status
            });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyLandlordEmail(VerifyLandlordEmailVm vm)
        {
            if (!ModelState.IsValid)
            {
                vm.Status = await TryGetOnboardingStatusAsync(vm.Email);
                return View(vm);
            }

            try
            {
                var req = new VerifyEmailOtpRequest
                {
                    Email = vm.Email,
                    EmailOtp = vm.EmailOtp
                };

                var res = await _api.PostAnonymousAsync<VerifyEmailOtpRequest, JsonElement>("Account/landlord-registration/verify-email", req);
                var nextStep = ReadString(res, "nextStep");
                TempData["AuthInfo"] = ReadString(res, "message") ?? "Email verified.";
                return RedirectToOnboardingStep(nextStep, vm.Email, vm.ReturnUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "VerifyLandlordEmail failed in Portal for {Email}", vm.Email);
                var apiError = ParseApiError(ex.Message);
                ModelState.AddModelError(string.Empty, SafeUserMessage(apiError.Message, "Email OTP verification failed. Please try again."));
                vm.Status = await TryGetOnboardingStatusAsync(vm.Email);
                return View(vm);
            }
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> LandlordCountry(string? email = null, string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToLocal(returnUrl);

            if (TempData["AuthInfo"] is string info)
                ViewBag.AuthInfo = info;

            if (TempData["AuthError"] is string error)
                ViewBag.AuthError = error;

            var status = await TryGetOnboardingStatusAsync(email);
            if (status != null && status.NextStep == LandlordOnboardingSteps.Email)
                return RedirectToAction(nameof(VerifyLandlordEmail), new { email = status.Email, returnUrl });

            if (status != null && status.NextStep != LandlordOnboardingSteps.Country)
                return RedirectToOnboardingStep(status.NextStep, status.Email, returnUrl);

            return View(new LandlordCountryVm
            {
                Email = email ?? status?.Email ?? string.Empty,
                CountryCode = string.IsNullOrWhiteSpace(status?.CountryCode) ? "+1" : status.CountryCode,
                CountryIsoCode = string.IsNullOrWhiteSpace(status?.CountryIsoCode)
                    ? ResolveCountryIsoFromDialingCode(status?.CountryCode)
                    : status.CountryIsoCode.Trim().ToUpperInvariant(),
                ReturnUrl = returnUrl,
                Status = status
            });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LandlordCountry(LandlordCountryVm vm)
        {
            if (!ModelState.IsValid)
            {
                vm.Status = await TryGetOnboardingStatusAsync(vm.Email);
                return View(vm);
            }

            try
            {
                var req = new UpsertLandlordCountryRequest
                {
                    Email = vm.Email,
                    CountryCode = vm.CountryCode,
                    CountryIsoCode = vm.CountryIsoCode
                };

                var res = await _api.PostAnonymousAsync<UpsertLandlordCountryRequest, JsonElement>("Account/landlord-registration/country", req);
                var nextStep = ReadString(res, "nextStep");
                TempData["AuthInfo"] = ReadString(res, "message") ?? "Country saved.";
                return RedirectToOnboardingStep(nextStep, vm.Email, vm.ReturnUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LandlordCountry failed in Portal for {Email}", vm.Email);
                var apiError = ParseApiError(ex.Message);
                ModelState.AddModelError(string.Empty, SafeUserMessage(apiError.Message, "Unable to save your country right now."));
                vm.Status = await TryGetOnboardingStatusAsync(vm.Email);
                return View(vm);
            }
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> VerifyLandlordPhone(string? email = null, string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToLocal(returnUrl);

            if (TempData["AuthInfo"] is string info)
                ViewBag.AuthInfo = info;

            if (TempData["AuthError"] is string error)
                ViewBag.AuthError = error;

            var status = await TryGetOnboardingStatusAsync(email);
            if (status != null && status.NextStep == LandlordOnboardingSteps.Email)
                return RedirectToAction(nameof(VerifyLandlordEmail), new { email = status.Email, returnUrl });
            if (status != null && status.NextStep == LandlordOnboardingSteps.Country)
                return RedirectToAction(nameof(LandlordCountry), new { email = status.Email, returnUrl });

            return View(new VerifyLandlordPhoneVm
            {
                Email = email ?? status?.Email ?? string.Empty,
                CountryCode = status?.CountryCode,
                PhoneNumber = ToLocalPhoneNumber(status?.PhoneNumber, status?.CountryCode),
                ReturnUrl = returnUrl,
                Status = status
            });
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> ContinueLandlordPhone(string? email = null, string? returnUrl = null)
        {
            var status = await TryGetOnboardingStatusAsync(email);
            if (status == null)
            {
                TempData["AuthError"] = "Unable to load your registration progress. Please sign in again to continue.";
                return RedirectToAction(nameof(Login), new { returnUrl });
            }

            return RedirectToOnboardingStep(status.NextStep, status.Email, returnUrl);
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveLandlordPhone(VerifyLandlordPhoneVm vm)
        {
            ModelState.Remove(nameof(vm.PhoneOtp));

            if (string.IsNullOrWhiteSpace(vm.Email))
                ModelState.AddModelError(nameof(vm.Email), "Email is required.");

            if (string.IsNullOrWhiteSpace(vm.PhoneNumber))
                ModelState.AddModelError(nameof(vm.PhoneNumber), "Phone number is required.");

            if (!ModelState.IsValid)
            {
                vm.Status = await TryGetOnboardingStatusAsync(vm.Email);
                return View(nameof(VerifyLandlordPhone), vm);
            }

            try
            {
                var req = new UpsertLandlordPhoneRequest
                {
                    Email = vm.Email,
                    CountryCode = vm.CountryCode?.Trim(),
                    PhoneNumber = vm.PhoneNumber.Trim()
                };

                var res = await _api.PostAnonymousAsync<UpsertLandlordPhoneRequest, JsonElement>("Account/landlord-registration/phone", req);
                TempData["AuthInfo"] = ReadString(res, "message") ?? "Phone OTP sent.";
                return RedirectToAction(nameof(VerifyLandlordPhone), new { email = vm.Email, returnUrl = vm.ReturnUrl });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SaveLandlordPhone failed in Portal for {Email}", vm.Email);
                var apiError = ParseApiError(ex.Message);
                ModelState.AddModelError(string.Empty, SafeUserMessage(apiError.Message, "Unable to save your phone number right now."));
                vm.Status = await TryGetOnboardingStatusAsync(vm.Email);
                return View(nameof(VerifyLandlordPhone), vm);
            }
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyLandlordPhone(VerifyLandlordPhoneVm vm)
        {
            ModelState.Remove(nameof(vm.CountryCode));
            ModelState.Remove(nameof(vm.PhoneNumber));

            if (string.IsNullOrWhiteSpace(vm.PhoneOtp))
                ModelState.AddModelError(nameof(vm.PhoneOtp), "Phone OTP is required.");

            if (!ModelState.IsValid)
            {
                vm.Status = await TryGetOnboardingStatusAsync(vm.Email);
                return View(vm);
            }

            try
            {
                var req = new VerifyPhoneOtpRequest
                {
                    Email = vm.Email,
                    PhoneOtp = vm.PhoneOtp?.Trim() ?? string.Empty
                };

                var res = await _api.PostAnonymousAsync<VerifyPhoneOtpRequest, JsonElement>("Account/landlord-registration/verify-phone", req);
                var nextStep = ReadString(res, "nextStep");
                TempData["AuthInfo"] = ReadString(res, "message") ?? "Phone verified.";
                return RedirectToOnboardingStep(nextStep, vm.Email, vm.ReturnUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "VerifyLandlordPhone failed in Portal for {Email}", vm.Email);
                var apiError = ParseApiError(ex.Message);
                ModelState.AddModelError(string.Empty, SafeUserMessage(apiError.Message, "Phone OTP verification failed. Please try again."));
                vm.Status = await TryGetOnboardingStatusAsync(vm.Email);
                return View(vm);
            }
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> LandlordMobilePayments(string? email = null, string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true && !User.IsInRole("Landlord"))
            {
                return RedirectToLocal(returnUrl);
            }

            if (TempData["AuthInfo"] is string info)
                ViewBag.AuthInfo = info;

            if (TempData["AuthError"] is string error)
                ViewBag.AuthError = error;

            if (User.Identity?.IsAuthenticated == true)
            {
                var overview = await _api.GetAsync<ProfileOverviewDto>("Account/profile-overview");
                return View(BuildLandlordMobilePaymentsVm(overview, returnUrl));
            }

            var resolvedEmail = ResolveLandlordUpdateEmail(email);
            var status = await TryGetOnboardingStatusAsync(resolvedEmail);
            if (status != null && status.NextStep is LandlordOnboardingSteps.Email or LandlordOnboardingSteps.Country or LandlordOnboardingSteps.Phone)
                return RedirectToOnboardingStep(status.NextStep, status.Email, returnUrl);

            return View(new LandlordMobilePaymentsVm
            {
                Email = resolvedEmail ?? status?.Email ?? string.Empty,
                UsePrimaryPhoneForSubscriptionPayments = status?.UsePrimaryPhoneForSubscriptionPayments ?? true,
                SubscriptionPaymentPhoneNumber = status?.SubscriptionPaymentPhoneNumber,
                SubscriptionPaymentChannel = status?.SubscriptionPaymentChannel ?? PayoutChannelEnum.MtnMoney,
                UsePrimaryPhoneForRentPayouts = status?.UsePrimaryPhoneForRentPayouts ?? true,
                PayoutPhoneNumber = status?.PayoutPhoneNumber,
                PayoutChannel = status?.PayoutChannel ?? PayoutChannelEnum.MtnMoney,
                WhatsAppPhoneNumber = status?.WhatsAppPhoneNumber,
                PrimaryPhoneNumber = status?.PhoneNumber,
                ReturnUrl = returnUrl,
                Status = status
            });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LandlordMobilePayments(LandlordMobilePaymentsVm vm)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                if (!User.IsInRole("Landlord"))
                {
                    return RedirectToLocal(vm.ReturnUrl);
                }

                var authenticatedEmail = ResolveAuthenticatedEmail();
                if (!string.IsNullOrWhiteSpace(authenticatedEmail))
                {
                    vm.Email = authenticatedEmail;
                    ModelState.Remove(nameof(vm.Email));
                }
            }

            if (!ModelState.IsValid)
            {
                vm.Status = await LoadMobilePaymentStatusAsync(vm.Email);
                vm.PrimaryPhoneNumber = vm.Status?.PhoneNumber;
                return View(vm);
            }

            try
            {
                var req = new UpsertLandlordMobilePaymentsRequest
                {
                    Email = vm.Email,
                    UsePrimaryPhoneForSubscriptionPayments = vm.UsePrimaryPhoneForSubscriptionPayments,
                    SubscriptionPaymentPhoneNumber = vm.SubscriptionPaymentPhoneNumber,
                    SubscriptionPaymentChannel = vm.SubscriptionPaymentChannel,
                    UsePrimaryPhoneForRentPayouts = vm.UsePrimaryPhoneForRentPayouts,
                    PayoutPhoneNumber = vm.PayoutPhoneNumber,
                    PayoutChannel = vm.PayoutChannel,
                    WhatsAppPhoneNumber = vm.WhatsAppPhoneNumber
                };

                var res = await _api.PostAnonymousAsync<UpsertLandlordMobilePaymentsRequest, JsonElement>("Account/landlord-registration/mobile-payments", req);
                var nextStep = ReadString(res, "nextStep");
                TempData["AuthInfo"] = ReadString(res, "message") ?? "Mobile payment details saved.";

                if (User.Identity?.IsAuthenticated == true)
                {
                    if (string.Equals(nextStep, LandlordOnboardingSteps.MobilePaymentVerification, StringComparison.OrdinalIgnoreCase))
                    {
                        return RedirectToAction(nameof(VerifyLandlordMobilePayments), new { email = vm.Email, returnUrl = vm.ReturnUrl });
                    }

                    TempData["Success"] = TempData["AuthInfo"];
                    TempData.Remove("AuthInfo");
                    return RedirectToLocal(vm.ReturnUrl);
                }

                return RedirectToOnboardingStep(nextStep, vm.Email, vm.ReturnUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LandlordMobilePayments failed in Portal for {Email}", vm.Email);
                var apiError = ParseApiError(ex.Message);
                ModelState.AddModelError(string.Empty, SafeUserMessage(apiError.Message, "Unable to save mobile payment details right now."));
                vm.Status = await LoadMobilePaymentStatusAsync(vm.Email);
                vm.PrimaryPhoneNumber = vm.Status?.PhoneNumber;
                return View(vm);
            }
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> VerifyLandlordMobilePayments(string? email = null, string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true && !User.IsInRole("Landlord"))
            {
                return RedirectToLocal(returnUrl);
            }

            if (TempData["AuthInfo"] is string info)
                ViewBag.AuthInfo = info;

            if (TempData["AuthError"] is string error)
                ViewBag.AuthError = error;

            if (User.Identity?.IsAuthenticated == true)
            {
                var overview = await _api.GetAsync<ProfileOverviewDto>("Account/profile-overview");
                if (!string.Equals(overview.NextOnboardingStep, LandlordOnboardingSteps.MobilePaymentVerification, StringComparison.OrdinalIgnoreCase))
                {
                    return RedirectToLocal(returnUrl);
                }

                return View(new VerifyLandlordMobilePaymentsVm
                {
                    Email = overview.Email,
                    ReturnUrl = returnUrl,
                    Status = BuildLandlordStatusFromProfileOverview(overview)
                });
            }

            var resolvedEmail = ResolveLandlordUpdateEmail(email);
            var status = await TryGetOnboardingStatusAsync(resolvedEmail);
            if (status != null && status.NextStep != LandlordOnboardingSteps.MobilePaymentVerification)
            {
                if (User.Identity?.IsAuthenticated == true)
                {
                    return RedirectToLocal(returnUrl);
                }

                return RedirectToOnboardingStep(status.NextStep, status.Email, returnUrl);
            }

            return View(new VerifyLandlordMobilePaymentsVm
            {
                Email = resolvedEmail ?? status?.Email ?? string.Empty,
                ReturnUrl = returnUrl,
                Status = status
            });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyLandlordMobilePayments(VerifyLandlordMobilePaymentsVm vm)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                if (!User.IsInRole("Landlord"))
                {
                    return RedirectToLocal(vm.ReturnUrl);
                }

                var authenticatedEmail = ResolveAuthenticatedEmail();
                if (!string.IsNullOrWhiteSpace(authenticatedEmail))
                {
                    vm.Email = authenticatedEmail;
                    ModelState.Remove(nameof(vm.Email));
                }
            }

            if (!ModelState.IsValid)
            {
                vm.Status = await LoadMobilePaymentStatusAsync(vm.Email);
                return View(vm);
            }

            try
            {
                var req = new VerifyMobilePaymentPhonesRequest
                {
                    Email = vm.Email,
                    SubscriptionPaymentOtp = vm.SubscriptionPaymentOtp,
                    PayoutOtp = vm.PayoutOtp,
                    WhatsAppOtp = vm.WhatsAppOtp
                };

                var res = await _api.PostAnonymousAsync<VerifyMobilePaymentPhonesRequest, JsonElement>("Account/landlord-registration/verify-mobile-payments", req);

                if (TryGetPropertyIgnoreCase(res, "token", out var tokenElement) && !string.IsNullOrWhiteSpace(tokenElement.GetString()))
                {
                    await _authSession.PersistTokenAsync(tokenElement.GetString()!);
                    TempData["Success"] = ReadString(res, "message") ?? "Registration complete.";
                    return RedirectToLocal(vm.ReturnUrl);
                }

                var nextStep = ReadString(res, "nextStep");
                TempData["AuthInfo"] = ReadString(res, "message") ?? "Verification updated.";

                if (User.Identity?.IsAuthenticated == true)
                {
                    if (string.Equals(nextStep, LandlordOnboardingSteps.MobilePaymentVerification, StringComparison.OrdinalIgnoreCase))
                    {
                        return RedirectToAction(nameof(VerifyLandlordMobilePayments), new { email = vm.Email, returnUrl = vm.ReturnUrl });
                    }

                    TempData["Success"] = TempData["AuthInfo"];
                    TempData.Remove("AuthInfo");
                    return RedirectToLocal(vm.ReturnUrl);
                }

                return RedirectToOnboardingStep(nextStep, vm.Email, vm.ReturnUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "VerifyLandlordMobilePayments failed in Portal for {Email}", vm.Email);
                var apiError = ParseApiError(ex.Message);
                ModelState.AddModelError(string.Empty, SafeUserMessage(apiError.Message, "Mobile payment OTP verification failed. Please try again."));
                vm.Status = await LoadMobilePaymentStatusAsync(vm.Email);
                return View(vm);
            }
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> LandlordKyc(string? email = null, string? returnUrl = null)
        {
            if (TempData["AuthInfo"] is string info)
                ViewBag.AuthInfo = info;

            if (TempData["AuthError"] is string error)
                ViewBag.AuthError = error;

            var status = await TryGetOnboardingStatusAsync(email);
            if (status != null &&
                status.NextStep is LandlordOnboardingSteps.Email
                    or LandlordOnboardingSteps.Country
                    or LandlordOnboardingSteps.Phone
                    or LandlordOnboardingSteps.MobilePayments
                    or LandlordOnboardingSteps.MobilePaymentVerification)
            {
                return RedirectToOnboardingStep(status.NextStep, status.Email, returnUrl);
            }

            if (status != null && status.IsKycApproved && status.PlatformTermsAccepted)
            {
                return User.Identity?.IsAuthenticated == true
                    ? RedirectToLocal(returnUrl)
                    : RedirectToAction(nameof(Login), new { returnUrl });
            }

            return View(new LandlordKycVm
            {
                Email = email ?? status?.Email ?? string.Empty,
                DocumentType = status?.KycDocumentType ?? KycDocumentTypeEnum.NationalId,
                ReturnUrl = returnUrl,
                Status = status
            });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LandlordKyc(LandlordKycVm vm)
        {
            vm.Status = await TryGetOnboardingStatusAsync(vm.Email);
            var isPartialResubmission =
                vm.Status?.KycStatus == LandlordKycStatusEnum.Rejected &&
                vm.Status.KycDocumentType == vm.DocumentType &&
                vm.Status.KycRejectedFiles.Any;

            var requireFaceFront = !isPartialResubmission || vm.Status!.KycRejectedFiles.FaceFront;
            var requireFaceRight = !isPartialResubmission || vm.Status!.KycRejectedFiles.FaceRight;
            var requireFaceLeft = !isPartialResubmission || vm.Status!.KycRejectedFiles.FaceLeft;
            var requireDocumentFront = !isPartialResubmission || vm.Status!.KycRejectedFiles.DocumentFront;
            var requireDocumentBack = DocumentBackRequired(vm.DocumentType) &&
                (!isPartialResubmission || vm.Status!.KycRejectedFiles.DocumentBack);

            if (requireFaceFront && vm.FaceFront == null)
                ModelState.AddModelError(nameof(vm.FaceFront), "Upload a front-facing photo.");
            if (requireFaceRight && vm.FaceRight == null)
                ModelState.AddModelError(nameof(vm.FaceRight), "Upload a photo looking right.");
            if (requireFaceLeft && vm.FaceLeft == null)
                ModelState.AddModelError(nameof(vm.FaceLeft), "Upload a photo looking left.");
            if (requireDocumentFront && vm.DocumentFront == null)
                ModelState.AddModelError(nameof(vm.DocumentFront), "Upload the front of your ID document.");
            if (requireDocumentBack && vm.DocumentBack == null)
                ModelState.AddModelError(nameof(vm.DocumentBack), "Upload the back of this document type.");

            if (!ModelState.IsValid)
            {
                return View(vm);
            }

            try
            {
                using var content = new MultipartFormDataContent();
                content.Add(new StringContent(vm.Email), "email");
                content.Add(new StringContent(((int)vm.DocumentType!.Value).ToString()), "documentType");
                AddFileIfPresent(content, vm.FaceFront, "faceFront");
                AddFileIfPresent(content, vm.FaceRight, "faceRight");
                AddFileIfPresent(content, vm.FaceLeft, "faceLeft");
                AddFileIfPresent(content, vm.DocumentFront, "documentFront");
                if (vm.DocumentBack != null)
                {
                    AddFile(content, vm.DocumentBack, "documentBack");
                }

                var res = await _api.PostAnonymousMultipartAsync<JsonElement>("Account/landlord-registration/kyc", content);
                var nextStep = ReadString(res, "nextStep");
                TempData["AuthInfo"] = ReadString(res, "message") ?? "Identity verification submitted.";
                return RedirectToOnboardingStep(nextStep, vm.Email, vm.ReturnUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LandlordKyc failed in Portal for {Email}", vm.Email);
                var apiError = ParseApiError(ex.Message);
                ModelState.AddModelError(string.Empty, SafeUserMessage(apiError.Message, "Unable to submit identity verification right now."));
                vm.Status = await TryGetOnboardingStatusAsync(vm.Email);
                return View(vm);
            }
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> LandlordContract(string? email = null, string? returnUrl = null)
        {
            if (TempData["AuthInfo"] is string info)
                ViewBag.AuthInfo = info;

            if (TempData["AuthError"] is string error)
                ViewBag.AuthError = error;

            var status = await TryGetOnboardingStatusAsync(email);
            if (status != null && status.NextStep != LandlordOnboardingSteps.Contract && status.NextStep != LandlordOnboardingSteps.Complete)
            {
                return RedirectToOnboardingStep(status.NextStep, status.Email, returnUrl);
            }

            if (status?.PlatformTermsAccepted == true)
            {
                return User.Identity?.IsAuthenticated == true
                    ? RedirectToLocal(returnUrl)
                    : RedirectToAction(nameof(Login), new { returnUrl });
            }

            return View(new LandlordContractVm
            {
                Email = email ?? status?.Email ?? string.Empty,
                SignatureName = status?.FullName ?? string.Empty,
                ReturnUrl = returnUrl,
                Status = status
            });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LandlordContract(LandlordContractVm vm)
        {
            if (!vm.Accepted)
                ModelState.AddModelError(nameof(vm.Accepted), "Accept the platform terms before signing.");

            if (!ModelState.IsValid)
            {
                vm.Status = await TryGetOnboardingStatusAsync(vm.Email);
                return View(vm);
            }

            try
            {
                var req = new SubmitLandlordContractRequest
                {
                    Email = vm.Email,
                    Accepted = vm.Accepted,
                    SignatureName = vm.SignatureName
                };

                var res = await _api.PostAnonymousAsync<SubmitLandlordContractRequest, JsonElement>("Account/landlord-registration/contract", req);
                if (TryGetPropertyIgnoreCase(res, "token", out var tokenElement) && !string.IsNullOrWhiteSpace(tokenElement.GetString()))
                {
                    await _authSession.PersistTokenAsync(tokenElement.GetString()!);
                    TempData["Success"] = ReadString(res, "message") ?? "Welcome to Lontsi Homes.";
                    return RedirectToLocal(vm.ReturnUrl);
                }

                var nextStep = ReadString(res, "nextStep");
                TempData["AuthInfo"] = ReadString(res, "message") ?? "Contract signed.";
                return RedirectToOnboardingStep(nextStep, vm.Email, vm.ReturnUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LandlordContract failed in Portal for {Email}", vm.Email);
                var apiError = ParseApiError(ex.Message);
                ModelState.AddModelError(string.Empty, SafeUserMessage(apiError.Message, "Unable to save your contract signature right now."));
                vm.Status = await TryGetOnboardingStatusAsync(vm.Email);
                return View(vm);
            }
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult RegisterVisitor(string? returnUrl = null)
        {
            TempData["AuthInfo"] = "Visitor messaging is temporarily hidden while we focus on the first release.";
            return RedirectToAction(nameof(Login), new { returnUrl });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegisterVisitor(RegisterVisitorVm vm)
        {
            if (!ModelState.IsValid)
            {
                return View(vm);
            }

            try
            {
                var result = await RegisterVisitorToApi(vm);

                TempData["AuthInfo"] = string.IsNullOrWhiteSpace(result.Message)
                    ? "Visitor account created. Check your email, phone number, and WhatsApp for OTP codes."
                    : result.Message;

                return RedirectToAction(nameof(VerifyVisitorAccount), new { email = result.Email ?? vm.Email, returnUrl = vm.ReturnUrl });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Visitor register failed in Portal for {Email}", vm.Email);

                var apiError = ParseApiError(ex.Message);
                ModelState.AddModelError(
                    string.Empty,
                    SafeUserMessage(apiError.Message, "Unable to create your visitor account right now. Please try again."));

                return View(vm);
            }
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult VerifyAccount(string? email = null, string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToLocal(returnUrl);

            if (TempData["AuthInfo"] is string info)
                ViewBag.AuthInfo = info;

            if (TempData["AuthError"] is string error)
                ViewBag.AuthError = error;

            return View(new VerifyAccountVm
            {
                Email = email ?? string.Empty,
                ReturnUrl = returnUrl
            });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyAccount(VerifyAccountVm vm)
        {
            if (!ModelState.IsValid)
                return View(vm);

            try
            {
                var req = new VerifyActivationOtpRequest
                {
                    Email = vm.Email,
                    EmailOtp = vm.EmailOtp,
                    PayoutOtp = vm.PayoutOtp,
                    WhatsAppOtp = vm.WhatsAppOtp
                };

                var res = await _api.PostAnonymousAsync<VerifyActivationOtpRequest, JsonElement>("Account/verify-activation-otp", req);

                if (!TryGetPropertyIgnoreCase(res, "token", out var tokenElement) || string.IsNullOrWhiteSpace(tokenElement.GetString()))
                {
                    ModelState.AddModelError(string.Empty, "Account verified. Please log in.");
                    return RedirectToAction(nameof(Login));
                }

                var token = tokenElement.GetString()!;
                await _authSession.PersistTokenAsync(token);

                TempData["Success"] = "Your account has been activated successfully.";
                return RedirectToLocal(vm.ReturnUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "VerifyAccount failed in Portal for {Email}", vm.Email);

                var apiError = ParseApiError(ex.Message);
                ModelState.AddModelError(
                    string.Empty,
                    SafeUserMessage(apiError.Message, "OTP verification failed. Please try again."));

                return View(vm);
            }
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult VerifyVisitorAccount(string? email = null, string? returnUrl = null)
        {
            TempData["AuthInfo"] = "Visitor messaging is temporarily hidden while we focus on the first release.";
            return RedirectToAction(nameof(Login), new { returnUrl });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyVisitorAccount(VerifyVisitorAccountVm vm)
        {
            if (!ModelState.IsValid)
                return View(vm);

            try
            {
                var req = new VerifyVisitorOtpRequest
                {
                    Email = vm.Email,
                    EmailOtp = vm.EmailOtp,
                    PhoneOtp = vm.PhoneOtp,
                    WhatsAppOtp = vm.WhatsAppOtp
                };

                var res = await _api.PostAnonymousAsync<VerifyVisitorOtpRequest, JsonElement>("Account/verify-visitor-otp", req);

                if (!TryGetPropertyIgnoreCase(res, "token", out var tokenElement) || string.IsNullOrWhiteSpace(tokenElement.GetString()))
                {
                    TempData["Success"] = "Visitor account verified. Please log in.";
                    return RedirectToAction(nameof(Login), new { returnUrl = vm.ReturnUrl });
                }

                var token = tokenElement.GetString()!;
                await _authSession.PersistTokenAsync(token);

                TempData["Success"] = "Your visitor account has been activated successfully.";
                return RedirectToLocal(vm.ReturnUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "VerifyVisitorAccount failed in Portal for {Email}", vm.Email);

                var apiError = ParseApiError(ex.Message);
                ModelState.AddModelError(
                    string.Empty,
                    SafeUserMessage(apiError.Message, "Visitor OTP verification failed. Please try again."));

                return View(vm);
            }
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResendActivationOtp(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                TempData["AuthError"] = "Email is required to resend OTP.";
                return RedirectToAction(nameof(VerifyAccount));
            }

            try
            {
                var req = new ResendActivationOtpRequest { Email = email };
                await _api.PostAnonymousAsync("Account/resend-activation-otp", req);
                TempData["AuthInfo"] = "A new email OTP has been sent. If mobile numbers are already configured, their pending OTPs may also be refreshed.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ResendActivationOtp failed in Portal for {Email}", email);

                var apiError = ParseApiError(ex.Message);
                TempData["AuthError"] = SafeUserMessage(apiError.Message, "Unable to resend OTP right now. Please try again.");
            }

            return RedirectToAction(nameof(VerifyLandlordEmail), new { email });
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ForgotPassword(string? email = null)
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToAction("Index", "Home");

            return View(new ForgotPasswordVm { Email = email?.Trim() ?? string.Empty });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordVm vm)
        {
            if (!ModelState.IsValid)
                return View(vm);

            try
            {
                await _api.PostAnonymousAsync<ForgotPasswordRequest>(
                    "Account/forgot-password",
                    new ForgotPasswordRequest { Email = vm.Email.Trim() });
            }
            catch (Exception ex)
            {
                // Keep this response identical to the success path so the portal cannot be
                // used to discover accounts or email-provider availability.
                _logger.LogWarning(ex, "Password recovery request could not be completed by the API.");
            }

            return RedirectToAction(nameof(ForgotPasswordConfirmation));
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ForgotPasswordConfirmation()
            => View();

        [HttpGet]
        [AllowAnonymous]
        public IActionResult ResetPassword(string email, string token)
        {
            Response.Headers.CacheControl = "no-store, no-cache";
            Response.Headers.Pragma = "no-cache";

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
            {
                TempData["AuthError"] = "This password reset link is invalid or incomplete. Request a new link and try again.";
                return RedirectToAction(nameof(Login));
            }

            return View(new ResetPasswordVm { Email = email.Trim(), Token = token });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(ResetPasswordVm vm)
        {
            Response.Headers.CacheControl = "no-store, no-cache";
            Response.Headers.Pragma = "no-cache";

            if (!ModelState.IsValid)
                return View(vm);

            try
            {
                await _api.PostAnonymousAsync<ResetPasswordRequest>(
                    "Account/reset-password",
                    new ResetPasswordRequest
                    {
                        Email = vm.Email.Trim(),
                        Token = vm.Token,
                        NewPassword = vm.NewPassword
                    });

                TempData["AuthInfo"] = "Your password has been reset. You can now sign in.";
                return RedirectToAction(nameof(Login), new { email = vm.Email.Trim() });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Password reset failed for an invalid or expired recovery link.");
                ModelState.AddModelError(string.Empty, "This password reset link is invalid or has expired. Request a new link and try again.");
                return View(vm);
            }
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult SetPassword(string email, string token)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
            {
                TempData["AuthError"] = "This password creation link is invalid.";
                return RedirectToAction(nameof(Login));
            }

            return View(new SetPasswordVm { Email = email, Token = token });
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetPassword(SetPasswordVm vm)
        {
            if (!ModelState.IsValid) return View(vm);
            try
            {
                await _api.PostAnonymousAsync<ResetPasswordRequest, JsonElement>(
                    "Account/set-invited-password",
                    new ResetPasswordRequest
                    {
                        Email = vm.Email,
                        Token = vm.Token,
                        NewPassword = vm.NewPassword
                    });
                TempData["AuthInfo"] = "Your password was created. You can now sign in.";
                return RedirectToAction(nameof(Login), new { email = vm.Email });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Set password failed for invited account {Email}.", vm.Email);
                ModelState.AddModelError(string.Empty, "This link is invalid or has expired. Ask the landlord to invite you again.");
                return View(vm);
            }
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResendVisitorOtp(string email, string? returnUrl = null)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                TempData["AuthError"] = "Email is required to resend visitor OTP.";
                return RedirectToAction(nameof(VerifyVisitorAccount), new { returnUrl });
            }

            try
            {
                var req = new ResendActivationOtpRequest { Email = email };
                await _api.PostAnonymousAsync("Account/resend-visitor-otp", req);
                TempData["AuthInfo"] = "New OTP codes have been sent to your email, phone number, and WhatsApp.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ResendVisitorOtp failed in Portal for {Email}", email);

                var apiError = ParseApiError(ex.Message);
                TempData["AuthError"] = SafeUserMessage(apiError.Message, "Unable to resend visitor OTP right now. Please try again.");
            }

            return RedirectToAction(nameof(VerifyVisitorAccount), new { email, returnUrl });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await _authSession.ClearAsync();
            return RedirectToAction(nameof(Login));
        }

        [HttpPost]
        [Authorize]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> KeepAlive()
        {
            try
            {
                var token = await _api.RefreshTokenAsync();
                if (string.IsNullOrWhiteSpace(token))
                {
                    await _authSession.ClearAsync();
                    return Unauthorized(new { Message = "Your session expired. Please sign in again." });
                }

                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KeepAlive failed in portal.");
                await _authSession.ClearAsync();
                return Unauthorized(new { Message = "Your session expired. Please sign in again." });
            }
        }

        [HttpGet]
        public IActionResult AccessDenied() => View();

        private async Task<string> LoginToApi(string email, string password)
        {
            var res = await _api.PostAnonymousAsync<object, JsonElement>("Account/login", new { Email = email, Password = password });

            if (!TryGetPropertyIgnoreCase(res, "token", out var t) || string.IsNullOrWhiteSpace(t.GetString()))
            {
                throw new Exception("Unable to sign in right now. Please try again.");
            }

            return t.GetString()!;
        }

        private async Task<RegisterApiResponse> RegisterToApi(RegisterVm vm)
        {
            var req = new StartLandlordRegistrationRequest
            {
                Email = vm.Email,
                Password = vm.Password,
                FirstName = vm.FirstName,
                LastName = vm.LastName,
                PlanId = vm.PlanId
            };

            var res = await _api.PostAnonymousAsync<StartLandlordRegistrationRequest, JsonElement>("Account/landlord-registration/start", req);

            var output = new RegisterApiResponse
            {
                RequiresActivation = true,
                Email = vm.Email
            };

            if (TryGetPropertyIgnoreCase(res, "token", out var token))
                output.Token = token.GetString();

            if (TryGetPropertyIgnoreCase(res, "requiresActivation", out var requiresActivation) &&
                (requiresActivation.ValueKind is JsonValueKind.True or JsonValueKind.False))
            {
                output.RequiresActivation = requiresActivation.GetBoolean();
            }

            if (TryGetPropertyIgnoreCase(res, "email", out var email))
                output.Email = email.GetString();

            if (TryGetPropertyIgnoreCase(res, "message", out var message))
                output.Message = message.GetString();

            if (TryGetPropertyIgnoreCase(res, "nextStep", out var nextStep))
                output.NextStep = nextStep.GetString();

            return output;
        }

        private async Task<RegisterApiResponse> RegisterVisitorToApi(RegisterVisitorVm vm)
        {
            var req = new RegisterVisitorRequest
            {
                Email = vm.Email,
                Password = vm.Password,
                FullName = vm.FullName,
                PhoneNumber = vm.PhoneNumber,
                WhatsAppPhoneNumber = vm.WhatsAppPhoneNumber
            };

            var res = await _api.PostAnonymousAsync<RegisterVisitorRequest, JsonElement>("Account/register-visitor", req);

            var output = new RegisterApiResponse
            {
                RequiresActivation = true,
                Email = vm.Email
            };

            if (TryGetPropertyIgnoreCase(res, "message", out var message))
                output.Message = message.GetString();

            if (TryGetPropertyIgnoreCase(res, "email", out var email))
                output.Email = email.GetString();

            return output;
        }

        private Task<List<SubscriptionPlanOptionVm>> GetPlansAsync()
        {
            return _api.GetAnonymousAsync<List<SubscriptionPlanOptionVm>>("Subscriptions/plans");
        }

        private async Task<LandlordOnboardingStatusDto?> TryGetOnboardingStatusAsync(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return null;
            }

            try
            {
                return await _api.GetAnonymousAsync<LandlordOnboardingStatusDto>(
                    $"Account/landlord-registration/status?email={Uri.EscapeDataString(email.Trim())}");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unable to load onboarding status for {Email}", email);
                return null;
            }
        }

        private async Task<LandlordOnboardingStatusDto?> LoadMobilePaymentStatusAsync(string? email)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                try
                {
                    var overview = await _api.GetAsync<ProfileOverviewDto>("Account/profile-overview");
                    return BuildLandlordStatusFromProfileOverview(overview);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Unable to load authenticated mobile payment status.");
                    return null;
                }
            }

            return await TryGetOnboardingStatusAsync(email);
        }

        private static LandlordMobilePaymentsVm BuildLandlordMobilePaymentsVm(ProfileOverviewDto overview, string? returnUrl)
        {
            return new LandlordMobilePaymentsVm
            {
                Email = overview.Email,
                UsePrimaryPhoneForSubscriptionPayments = overview.UsePrimaryPhoneForSubscriptionPayments,
                SubscriptionPaymentPhoneNumber = overview.SubscriptionPaymentPhoneNumber,
                SubscriptionPaymentChannel = overview.SubscriptionPaymentChannel ?? PayoutChannelEnum.MtnMoney,
                UsePrimaryPhoneForRentPayouts = overview.UsePrimaryPhoneForRentPayouts,
                PayoutPhoneNumber = overview.PayoutPhoneNumber,
                PayoutChannel = overview.PayoutChannel ?? PayoutChannelEnum.MtnMoney,
                WhatsAppPhoneNumber = overview.WhatsAppPhoneNumber,
                PrimaryPhoneNumber = overview.PhoneNumber,
                ReturnUrl = returnUrl,
                Status = BuildLandlordStatusFromProfileOverview(overview)
            };
        }

        private static string ResolveCountryIsoFromDialingCode(string? countryCode)
        {
            var normalized = NormalizeDialingCode(countryCode);
            return normalized switch
            {
                "+237" => "CM",
                "+44" => "GB",
                "+33" => "FR",
                "+32" => "BE",
                "+49" => "DE",
                "+234" => "NG",
                "+225" => "CI",
                "+233" => "GH",
                "+27" => "ZA",
                "+254" => "KE",
                "+971" => "AE",
                _ => "CA"
            };
        }

        private static string NormalizeDialingCode(string? countryCode)
        {
            var digits = new string((countryCode ?? string.Empty).Where(char.IsDigit).ToArray());
            return string.IsNullOrWhiteSpace(digits) ? string.Empty : $"+{digits}";
        }

        private static LandlordOnboardingStatusDto BuildLandlordStatusFromProfileOverview(ProfileOverviewDto overview)
        {
            return new LandlordOnboardingStatusDto
            {
                UserId = overview.UserId,
                Email = overview.Email,
                FirstName = overview.FirstName,
                LastName = overview.LastName,
                FullName = overview.FullName,
                CountryCode = overview.CountryCode,
                CountryIsoCode = overview.CountryIsoCode,
                PhoneNumber = overview.PhoneNumber,
                EmailConfirmed = true,
                PhoneNumberConfirmed = overview.SmsVerificationEnabled
                    ? !string.IsNullOrWhiteSpace(overview.PhoneNumber)
                    : true,
                UsePrimaryPhoneForSubscriptionPayments = overview.UsePrimaryPhoneForSubscriptionPayments,
                SubscriptionPaymentPhoneNumber = overview.SubscriptionPaymentPhoneNumber,
                SubscriptionPaymentChannel = overview.SubscriptionPaymentChannel,
                IsSubscriptionPaymentPhoneVerified = overview.IsSubscriptionPaymentPhoneVerified,
                UsePrimaryPhoneForRentPayouts = overview.UsePrimaryPhoneForRentPayouts,
                PayoutPhoneNumber = overview.PayoutPhoneNumber,
                PayoutChannel = overview.PayoutChannel,
                IsPayoutPhoneVerified = overview.IsPayoutPhoneVerified,
                WhatsAppPhoneNumber = overview.WhatsAppPhoneNumber,
                IsWhatsAppPhoneVerified = overview.IsWhatsAppPhoneVerified,
                SubscriptionPaymentOtpRequestLimit = overview.SubscriptionPaymentOtpRequestLimit,
                PayoutOtpRequestLimit = overview.PayoutOtpRequestLimit,
                WhatsAppOtpRequestLimit = overview.WhatsAppOtpRequestLimit,
                SmsVerificationEnabled = overview.SmsVerificationEnabled,
                KycDocumentType = overview.KycDocumentType,
                KycStatus = overview.KycStatus,
                IsKycSubmitted = overview.IsKycSubmitted,
                IsKycApproved = overview.IsKycApproved,
                KycSubmittedAt = overview.KycSubmittedAt,
                KycReviewedAt = overview.KycReviewedAt,
                KycReviewNote = overview.KycReviewNote,
                KycRejectedFiles = overview.KycRejectedFiles,
                PlatformTermsAccepted = overview.PlatformTermsAccepted,
                PlatformTermsAcceptedAt = overview.PlatformTermsAcceptedAt,
                PlatformTermsSignatureName = overview.PlatformTermsSignatureName,
                PlatformTermsVersion = overview.PlatformTermsVersion,
                NextStep = overview.NextOnboardingStep,
                IsComplete = string.Equals(overview.NextOnboardingStep, LandlordOnboardingSteps.Complete, StringComparison.OrdinalIgnoreCase),
                Roles = overview.Roles
            };
        }

        private string? ResolveLandlordUpdateEmail(string? email)
        {
            return User.Identity?.IsAuthenticated == true
                ? ResolveAuthenticatedEmail() ?? email
                : email;
        }

        private string? ResolveAuthenticatedEmail()
        {
            return User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name;
        }

        private IActionResult RedirectToOnboardingStep(string? nextStep, string email, string? returnUrl)
        {
            var route = new { email, returnUrl };
            return (nextStep ?? LandlordOnboardingSteps.Email) switch
            {
                LandlordOnboardingSteps.Country => RedirectToAction(nameof(LandlordCountry), route),
                LandlordOnboardingSteps.Phone => RedirectToAction(nameof(VerifyLandlordPhone), route),
                LandlordOnboardingSteps.MobilePayments => RedirectToAction(nameof(LandlordMobilePayments), route),
                LandlordOnboardingSteps.MobilePaymentVerification => RedirectToAction(nameof(VerifyLandlordMobilePayments), route),
                LandlordOnboardingSteps.Kyc => RedirectToAction(nameof(LandlordKyc), route),
                LandlordOnboardingSteps.Contract => RedirectToAction(nameof(LandlordContract), route),
                LandlordOnboardingSteps.Complete => RedirectToAction(nameof(Login), new { returnUrl }),
                _ => RedirectToAction(nameof(VerifyLandlordEmail), route)
            };
        }

        private static void AddFile(MultipartFormDataContent content, IFormFile file, string name)
        {
            var streamContent = new StreamContent(file.OpenReadStream());
            if (!string.IsNullOrWhiteSpace(file.ContentType))
            {
                streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
            }

            content.Add(streamContent, name, file.FileName);
        }

        private static void AddFileIfPresent(MultipartFormDataContent content, IFormFile? file, string name)
        {
            if (file != null)
            {
                AddFile(content, file, name);
            }
        }

        private static bool DocumentBackRequired(KycDocumentTypeEnum? documentType)
        {
            return documentType is KycDocumentTypeEnum.NationalId
                or KycDocumentTypeEnum.DriverLicense
                or KycDocumentTypeEnum.ResidencePermit
                or KycDocumentTypeEnum.Other;
        }

        private static string? ReadString(JsonElement element, string propertyName)
        {
            return TryGetPropertyIgnoreCase(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
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
            {
                return fallback;
            }

            return LooksTechnicalMessage(apiMessage) ? fallback : apiMessage;
        }

        private static string ToLocalPhoneNumber(string? phoneNumber, string? countryCode)
        {
            var digits = new string((phoneNumber ?? string.Empty).Where(char.IsDigit).ToArray());
            if (digits.StartsWith("00", StringComparison.Ordinal))
            {
                digits = digits[2..];
            }

            var countryDigits = new string((countryCode ?? string.Empty).Where(char.IsDigit).ToArray());
            if (!string.IsNullOrWhiteSpace(countryDigits) &&
                digits.StartsWith(countryDigits, StringComparison.Ordinal))
            {
                digits = digits[countryDigits.Length..];
            }

            return digits;
        }

        private static bool LooksTechnicalMessage(string message)
        {
            var normalized = message.Trim();
            if (normalized.Length == 0)
            {
                return true;
            }

            var technicalFragments = new[]
            {
                "exception",
                "stack trace",
                "inner exception",
                "dbupdateexception",
                "sqlexception",
                "invalid column name",
                "entity changes",
                "microsoft.entityframeworkcore",
                " at "
            };

            return technicalFragments.Any(fragment =>
                normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }

        private static ApiErrorPayload ParseApiError(string raw)
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

                string? ReadString(string key)
                {
                    if (root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty(key, out var value) &&
                        value.ValueKind == JsonValueKind.String)
                    {
                        return value.GetString();
                    }

                    return null;
                }

                return new ApiErrorPayload
                {
                    Code = ReadString("Code") ?? ReadString("code"),
                    Message = ReadString("Message") ?? ReadString("message"),
                    Email = ReadString("Email") ?? ReadString("email"),
                    NextStep = ReadString("NextStep") ?? ReadString("nextStep")
                };
            }
            catch
            {
                return new ApiErrorPayload { Message = raw };
            }
        }

        private sealed class RegisterApiResponse
        {
            public bool RequiresActivation { get; set; }
            public string? Token { get; set; }
            public string? Email { get; set; }
            public string? Message { get; set; }
            public string? NextStep { get; set; }
        }

        private sealed class ApiErrorPayload
        {
            public string? Code { get; set; }
            public string? Message { get; set; }
            public string? Email { get; set; }
            public string? NextStep { get; set; }
        }

        private IActionResult RedirectToLocal(string? returnUrl)
        {
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction("Index", "Home");
        }
    }
}








