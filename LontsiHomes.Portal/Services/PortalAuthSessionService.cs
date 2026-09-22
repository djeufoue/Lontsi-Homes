using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace LontsiHomes.Portal.Services
{
    public class PortalAuthSessionService
    {
        private const string JwtTokenSessionKey = "JWT_TOKEN";
        private const string JwtTokenClaimType = "jwt_token";

        private readonly IHttpContextAccessor _httpContextAccessor;

        public PortalAuthSessionService(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public string? GetCurrentToken()
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext == null)
            {
                return null;
            }

            var token = httpContext.Session.GetString(JwtTokenSessionKey);
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }

            if (httpContext.User.Identity?.IsAuthenticated == true)
            {
                token = httpContext.User.FindFirst(JwtTokenClaimType)?.Value;
                if (!string.IsNullOrWhiteSpace(token))
                {
                    httpContext.Session.SetString(JwtTokenSessionKey, token);
                }
            }

            return token;
        }

        public bool IsTokenExpiringSoon(string token, TimeSpan threshold)
        {
            var expiryUtc = GetTokenExpiryUtc(token);
            return expiryUtc.HasValue && expiryUtc.Value <= DateTimeOffset.UtcNow.Add(threshold);
        }

        public async Task PersistTokenAsync(string token)
        {
            var httpContext = _httpContextAccessor.HttpContext
                ?? throw new InvalidOperationException("No active HTTP context is available.");

            httpContext.Session.SetString(JwtTokenSessionKey, token);

            var principal = BuildPrincipalFromJwt(token);
            await httpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    AllowRefresh = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
                    IsPersistent = true
                });

            httpContext.User = principal;
        }

        public async Task ClearAsync()
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext == null)
            {
                return;
            }

            httpContext.Session.Remove(JwtTokenSessionKey);
            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }

        private static DateTimeOffset? GetTokenExpiryUtc(string token)
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                if (!handler.CanReadToken(token))
                {
                    return null;
                }

                var jwt = handler.ReadJwtToken(token);
                if (jwt.ValidTo == DateTime.MinValue)
                {
                    return null;
                }

                return new DateTimeOffset(DateTime.SpecifyKind(jwt.ValidTo, DateTimeKind.Utc));
            }
            catch
            {
                return null;
            }
        }

        private static ClaimsPrincipal BuildPrincipalFromJwt(string token)
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(token))
            {
                throw new InvalidOperationException("Received an invalid authentication token.");
            }

            var jwt = handler.ReadJwtToken(token);
            var claims = jwt.Claims.Select(c => new Claim(c.Type, c.Value)).ToList();

            var sub = claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value;
            if (!string.IsNullOrWhiteSpace(sub) && !claims.Any(c => c.Type == ClaimTypes.NameIdentifier))
            {
                claims.Add(new Claim(ClaimTypes.NameIdentifier, sub));
            }

            var email = claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value
                        ?? claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Email)?.Value;
            if (!string.IsNullOrWhiteSpace(email) && !claims.Any(c => c.Type == ClaimTypes.Email))
            {
                claims.Add(new Claim(ClaimTypes.Email, email));
            }

            if (!claims.Any(c => c.Type == ClaimTypes.Name) && !string.IsNullOrWhiteSpace(email))
            {
                claims.Add(new Claim(ClaimTypes.Name, email));
            }

            var roleValues = claims
                .Where(c => c.Type == ClaimTypes.Role || c.Type == "role")
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var role in roleValues)
            {
                if (!claims.Any(c => c.Type == ClaimTypes.Role && string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase)))
                {
                    claims.Add(new Claim(ClaimTypes.Role, role));
                }
            }

            if (!claims.Any(c => c.Type == JwtTokenClaimType))
            {
                claims.Add(new Claim(JwtTokenClaimType, token));
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
