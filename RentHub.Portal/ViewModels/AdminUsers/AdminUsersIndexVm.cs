using Common.CommunicationModels;

namespace RentHub.Portal.ViewModels.AdminUsers
{
    public class AdminUsersIndexVm
    {
        public string? Search { get; set; }
        public bool CanDeleteUsers { get; set; }
        public bool SkipLandlordPhoneVerification { get; set; }
        public List<AdminUserVerificationStatusDto> Users { get; set; } = new();
    }

    public class AdminLandlordsIndexVm
    {
        public string? Search { get; set; }
        public bool CanDeleteUsers { get; set; }
        public List<AdminUserVerificationStatusDto> Landlords { get; set; } = new();
    }

    public class AdminUserOverviewVm
    {
        public AdminUserOverviewDto Overview { get; set; } = new();
    }

    public class AdminLandlordApprovalsVm
    {
        public string? Search { get; set; }
        public List<AdminLandlordApprovalDto> Landlords { get; set; } = new();
    }
}
