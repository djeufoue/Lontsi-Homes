using Common.Enums;

namespace RentHub.Portal.ViewModels.Profile
{
    public sealed class UpdateEmailLanguageVm
    {
        public PlatformLanguage EmailLanguage { get; set; } = PlatformLanguage.English;
    }
}
