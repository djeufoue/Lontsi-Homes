using Common.CommunicationModels;

namespace RentHub.Portal.ViewModels.Profile
{
    public class ProfileIndexVm
    {
        public ProfileOverviewDto Overview { get; set; } = new();
        public DateTimeOffset NowUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}
