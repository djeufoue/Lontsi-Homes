using Common.Enums;
using Microsoft.AspNetCore.Localization;

namespace LontsiHomes.Portal.Localization
{
    public sealed class UserPreferenceRequestCultureProvider : RequestCultureProvider
    {
        public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            var language = httpContext.User.Identity?.IsAuthenticated == true
                ? PlatformLanguageOptions.FromClaim(
                    httpContext.User.FindFirst(PlatformLanguageOptions.ClaimType)?.Value)
                : PlatformLanguage.English;

            var cultureName = language.ToCultureName();
            return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(cultureName, cultureName));
        }
    }
}
