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

namespace RentHub.Portal.Controllers
{
    public class AuthController : Controller
    {
        private readonly RentHubApiClient _api;
        private readonly ILogger<AuthController> _logger;

        public AuthController(RentHubApiClient api, ILogger<AuthController> logger)
        {
            _api = api;
            _logger = logger;
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult Login()
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToAction("Index", "Home");

            if (TempData["AuthInfo"] is string info)
                ViewBag.AuthInfo = info;

            return View(new LoginVm());
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
                var principal = BuildPrincipalFromJwt(token);
                await SignInWithJwt(token, principal);

                return RedirectToAction("Index", "Home");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Login failed in Portal for {Email}", vm.Email);

                var apiError = ParseApiError(ex.Message);
                if (string.Equals(apiError.Code, "EMAIL_NOT_CONFIRMED", StringComparison.OrdinalIgnoreCase))
                {
                    TempData["AuthInfo"] = SafeUserMessage(
                        apiError.Message,
                        "Your account is not activated yet. Enter the OTP sent to your email.");

                    return RedirectToAction(nameof(VerifyAccount), new { email = apiError.Email ?? vm.Email });
                }

                ModelState.AddModelError(
                    string.Empty,
                    SafeUserMessage(apiError.Message, "Unable to sign in right now. Please try again."));

                return View(vm);
            }
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> Register()
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToAction("Index", "Home");

            ViewBag.Plans = await GetPlansAsync();
            return View(new RegisterVm());
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

                    return RedirectToAction(nameof(VerifyAccount), new { email = result.Email ?? vm.Email });
                }

                var principal = BuildPrincipalFromJwt(result.Token);
                await SignInWithJwt(result.Token, principal);

                return RedirectToAction("Index", "Home");
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
        public IActionResult VerifyAccount(string? email = null)
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToAction("Index", "Home");

            if (TempData["AuthInfo"] is string info)
                ViewBag.AuthInfo = info;

            if (TempData["AuthError"] is string error)
                ViewBag.AuthError = error;

            return View(new VerifyAccountVm
            {
                Email = email ?? string.Empty
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
                    Otp = vm.Otp
                };

                var res = await _api.PostAsync<VerifyActivationOtpRequest, JsonElement>("Account/verify-activation-otp", req);

                if (!TryGetPropertyIgnoreCase(res, "token", out var tokenElement) || string.IsNullOrWhiteSpace(tokenElement.GetString()))
                {
                    ModelState.AddModelError(string.Empty, "Account verified. Please log in.");
                    return RedirectToAction(nameof(Login));
                }

                var token = tokenElement.GetString()!;
                var principal = BuildPrincipalFromJwt(token);
                await SignInWithJwt(token, principal);

                TempData["Success"] = "Your account has been activated successfully.";
                return RedirectToAction("Index", "Home");
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
                await _api.PostAsync("Account/resend-activation-otp", req);
                TempData["AuthInfo"] = "A new OTP has been sent to your email.";
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
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            HttpContext.Session.Remove("JWT_TOKEN");
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(Login));
        }

        [HttpGet]
        public IActionResult AccessDenied() => View();

        private async Task<string> LoginToApi(string email, string password)
        {
            var res = await _api.PostAsync<object, JsonElement>("Account/login", new { Email = email, Password = password });

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
                PlanId = vm.PlanId
            };

            var res = await _api.PostAsync<RegisterRequest, JsonElement>("Account/register", req);

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

        private async Task SignInWithJwt(string token, ClaimsPrincipal principal)
        {
            const string jwtTokenClaimType = "jwt_token";

            HttpContext.Session.SetString("JWT_TOKEN", token);

            if (principal.Identity is ClaimsIdentity identity &&
                !identity.HasClaim(c => c.Type == jwtTokenClaimType))
            {
                identity.AddClaim(new Claim(jwtTokenClaimType, token));
            }

            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        }

        private Task<List<SubscriptionPlanOptionVm>> GetPlansAsync()
        {
            return _api.GetAsync<List<SubscriptionPlanOptionVm>>("Subscriptions/plans");
        }

        private static ClaimsPrincipal BuildPrincipalFromJwt(string token)
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(token))
                throw new Exception("Received an invalid authentication token.");

            var jwt = handler.ReadJwtToken(token);
            var claims = jwt.Claims.Select(c => new Claim(c.Type, c.Value)).ToList();

            var sub = claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value;
            if (!string.IsNullOrWhiteSpace(sub) && !claims.Any(c => c.Type == ClaimTypes.NameIdentifier))
                claims.Add(new Claim(ClaimTypes.NameIdentifier, sub));

            var email = claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value
                        ?? claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Email)?.Value;
            if (!string.IsNullOrWhiteSpace(email) && !claims.Any(c => c.Type == ClaimTypes.Email))
                claims.Add(new Claim(ClaimTypes.Email, email));

            if (!claims.Any(c => c.Type == ClaimTypes.Name) && !string.IsNullOrWhiteSpace(email))
                claims.Add(new Claim(ClaimTypes.Name, email));

            var roleValues = claims
                .Where(c => c.Type == ClaimTypes.Role || c.Type == "role")
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var role in roleValues)
            {
                if (!claims.Any(c => c.Type == ClaimTypes.Role && string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase)))
                    claims.Add(new Claim(ClaimTypes.Role, role));
            }

            var identity = new ClaimsIdentity(
                claims,
                CookieAuthenticationDefaults.AuthenticationScheme,
                ClaimTypes.Name,
                ClaimTypes.Role);

            return new ClaimsPrincipal(identity);
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
    }
}








