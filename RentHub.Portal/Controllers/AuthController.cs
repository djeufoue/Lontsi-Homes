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

        public AuthController(RentHubApiClient api)
        {
            _api = api;
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult Login()
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToAction("Index", "Home");

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
                ModelState.AddModelError(string.Empty, ex.Message);
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
                var token = await RegisterToApi(vm);
                var principal = BuildPrincipalFromJwt(token);
                await SignInWithJwt(token, principal);

                return RedirectToAction("Index", "Home");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                ViewBag.Plans = await GetPlansAsync();
                return View(vm);
            }
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
            if (!res.TryGetProperty("Token", out var t)) throw new Exception("Token missing from API response.");
            return t.GetString()!;
        }

        private async Task<string> RegisterToApi(RegisterVm vm)
        {
            var req = new RegisterRequest
            {
                Email = vm.Email,
                Password = vm.Password,
                FullName = vm.FullName,
                CountryCode = vm.CountryCode ?? string.Empty,
                PlanId = vm.PlanId
            };

            var res = await _api.PostAsync<RegisterRequest, JsonElement>("Account/register", req);
            if (!res.TryGetProperty("Token", out var t))
                throw new Exception("Token missing from API response.");

            return t.GetString()!;
        }

        private async Task SignInWithJwt(string token, ClaimsPrincipal principal)
        {
            HttpContext.Session.SetString("JWT_TOKEN", token);
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
    }
}
