using Common.CommunicationModels;

namespace RentHub.Portal.ViewModels.AdminUsers
{
    public class AdminUsersIndexVm
    {
        public string? Search { get; set; }
        public bool CanDeleteUsers { get; set; }
        public List<AdminUserVerificationStatusDto> Users { get; set; } = new();
    }

    public class AdminUserOverviewVm
    {
        public AdminUserOverviewDto Overview { get; set; } = new();
    }
}
