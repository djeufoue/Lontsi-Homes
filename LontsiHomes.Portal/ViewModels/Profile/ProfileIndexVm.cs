using Common.CommunicationModels;

namespace LontsiHomes.Portal.ViewModels.Profile
{
    public class ProfileIndexVm
    {
        public ProfileOverviewDto Overview { get; set; } = new();
        public DateTimeOffset NowUtc { get; set; } = DateTimeOffset.UtcNow;
        public bool IsOwnProfile { get; set; } = true;
        public int? AccessTenancyId { get; set; }
        public WhatsAppPreferenceDto? WhatsAppPreference { get; set; }
    }
}
