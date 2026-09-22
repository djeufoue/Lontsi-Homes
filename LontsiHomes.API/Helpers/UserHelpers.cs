using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;

namespace LontsiHomes.API.Helpers
{
    public static class UserHelpers
    {
        public static string? GetUserId(ClaimsPrincipal user)
        {
            return user.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? user.FindFirstValue(JwtRegisteredClaimNames.Sub)
                ?? user.FindFirstValue(JwtRegisteredClaimNames.NameId)
                ?? user.FindFirstValue("nameid");
        }
    }
}
