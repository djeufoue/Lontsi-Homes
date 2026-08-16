using System;
using System.Collections.Generic;
using System.Globalization;

namespace Common.Enums
{
    public enum PlatformLanguage
    {
        English = 0,
        French = 1
    }

    public static class PlatformLanguageOptions
    {
        public const string ClaimType = "platform_language";
        public const string EnglishCultureName = "en-CA";
        public const string FrenchCultureName = "fr-CA";

        public static IReadOnlyList<CultureInfo> SupportedCultures { get; } =
            new[]
            {
                CultureInfo.GetCultureInfo(EnglishCultureName),
                CultureInfo.GetCultureInfo(FrenchCultureName)
            };

        public static string ToCultureName(this PlatformLanguage language) => language switch
        {
            PlatformLanguage.English => EnglishCultureName,
            PlatformLanguage.French => FrenchCultureName,
            _ => EnglishCultureName
        };

        public static bool IsSupported(PlatformLanguage language)
            => language is PlatformLanguage.English or PlatformLanguage.French;

        public static PlatformLanguage FromClaim(string? value)
        {
            if (Enum.TryParse<PlatformLanguage>(value, ignoreCase: true, out var language) &&
                IsSupported(language))
            {
                return language;
            }

            return PlatformLanguage.English;
        }
    }
}
