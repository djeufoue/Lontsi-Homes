using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Common.Enums;
using RentHub.API.Models.Entities;
using RentHub.API.Models.Settings;
using RentHub.API.Data;
using Microsoft.EntityFrameworkCore;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace RentHub.API.Services.Auth
{
    /// <summary>
    /// Generates JWT tokens for authenticated users.  Tokens include the user's
    /// identifier, email and roles.
    /// </summary>
    public class TokenService
    {
        private readonly JwtSettings _jwtSettings;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApplicationDbContext _context;

        public TokenService(
            IOptions<JwtSettings> jwtOptions,
            UserManager<ApplicationUser> userManager,
            ApplicationDbContext context)
        {
            _jwtSettings = jwtOptions.Value;
            _userManager = userManager;
            _context = context;
        }

        public async Task<IReadOnlyList<string>> GetEffectiveRolesAsync(ApplicationUser user)
        {
            var roles = (await _userManager.GetRolesAsync(user)).ToList();
            if (roles.Any(role => string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase)))
            {
                var hasActiveManagerAssignment = await _context.PropertyManagerAssignments
                    .AnyAsync(assignment => !assignment.IsDeleted && assignment.ManagerId == user.Id) ||
                    await _context.ApartmentOwners.AnyAsync(assignment =>
                        !assignment.IsDeleted &&
                        assignment.OwnerId == user.Id &&
                        assignment.Role == ApartmentMemberRoleEnum.Manager);

                if (!hasActiveManagerAssignment)
                {
                    roles.RemoveAll(role => string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase));
                }
            }

            return roles;
        }

        public async Task<string> GenerateTokenAsync(ApplicationUser user)
        {
            var authClaims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
                new Claim(ClaimTypes.Email, user.Email ?? string.Empty),
                new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(user.FullName) ? (user.Email ?? string.Empty) : user.FullName),
                new Claim("subscription_exempt", user.IsSubscriptionExempt ? "true" : "false"),
                new Claim(PlatformLanguageOptions.ClaimType, user.Language.ToString()),
                new Claim("security_stamp", user.SecurityStamp ?? string.Empty),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };
            var roles = await GetEffectiveRolesAsync(user);
            authClaims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

            var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.SigningKey));
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Issuer = _jwtSettings.Issuer,
                Audience = _jwtSettings.Audience,
                Expires = DateTime.UtcNow.AddMinutes(_jwtSettings.ExpiryMinutes),
                Subject = new ClaimsIdentity(authClaims),
                SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)
            };
            var tokenHandler = new JwtSecurityTokenHandler();
            var securityToken = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(securityToken);
        }
    }
}
