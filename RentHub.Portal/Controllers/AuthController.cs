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
        public IActionResult Login(string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToLocal(returnUrl);

            if (TempData["AuthInfo"] is string info)
                ViewBag.AuthInfo = info;

            return View(new LoginVm { ReturnUrl = returnUrl });
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
                if (string.Equals(apiError.Code, "EMAIL_NOT_CONFIRMED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(apiError.Code, "ACCOUNT_VERIFICATION_PENDING", StringComparison.OrdinalIgnoreCase))
                {
                    TempData["AuthInfo"] = SafeUserMessage(
                        apiError.Message,
                        "Your account is not activated yet. Enter the OTP codes sent to your email, payout number, and WhatsApp.");

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
            return View(new RegisterVm { ReturnUrl = returnUrl });
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

                    return RedirectToAction(nameof(VerifyAccount), new { email = result.Email ?? vm.Email, returnUrl = vm.ReturnUrl });
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
        public IActionResult RegisterVisitor(string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToLocal(returnUrl);

            return View(new RegisterVisitorVm { ReturnUrl = returnUrl });
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
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToLocal(returnUrl);

            if (TempData["AuthInfo"] is string info)
                ViewBag.AuthInfo = info;

            if (TempData["AuthError"] is string error)
                ViewBag.AuthError = error;

            return View(new VerifyVisitorAccountVm
            {
                Email = email ?? string.Empty,
                ReturnUrl = returnUrl
            });
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
                TempData["AuthInfo"] = "New OTP codes have been sent to your email, payout number, and WhatsApp.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ResendActivationOtp failed in Portal for {Email}", email);

                var apiError = ParseApiError(ex.Message);
                TempData["AuthError"] = SafeUserMessage(apiError.Message, "Unable to resend OTP right now. Please try again.");
            }

            return RedirectToAction(nameof(VerifyAccount), new { email });
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
            var req = new RegisterRequest
            {
                Email = vm.Email,
                Password = vm.Password,
                FirstName = vm.FirstName,
                LastName = vm.LastName,
                CountryCode = vm.CountryCode,
                PhoneNumber = vm.PhoneNumber,
                PayoutPhoneNumber = vm.PayoutPhoneNumber,
                PayoutChannel = vm.PayoutChannel,
                WhatsAppPhoneNumber = vm.WhatsAppPhoneNumber,
                PlanId = vm.PlanId
            };

            var res = await _api.PostAnonymousAsync<RegisterRequest, JsonElement>("Account/register", req);

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
                    Email = ReadString("Email") ?? ReadString("email")
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
        }

        private sealed class ApiErrorPayload
        {
            public string? Code { get; set; }
            public string? Message { get; set; }
            public string? Email { get; set; }
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








