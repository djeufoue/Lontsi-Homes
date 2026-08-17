using Common.Enums;

namespace Common.CommunicationModels
{
    public sealed class UpdatePlatformLanguageRequest
    {
        public PlatformLanguage Language { get; set; } = PlatformLanguage.English;
    }

    public sealed class UpdatePlatformLanguageResponse
    {
        public PlatformLanguage Language { get; set; } = PlatformLanguage.English;
        public string CultureName { get; set; } = PlatformLanguageOptions.EnglishCultureName;
        public string Token { get; set; } = string.Empty;
    }

    public sealed class UpdateEmailLanguageRequest
    {
        public PlatformLanguage EmailLanguage { get; set; } = PlatformLanguage.English;
    }

    public sealed class UpdateEmailLanguageResponse
    {
        public PlatformLanguage EmailLanguage { get; set; } = PlatformLanguage.English;
    }
}
