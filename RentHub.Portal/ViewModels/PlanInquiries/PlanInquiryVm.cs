using System.ComponentModel.DataAnnotations;
using Common.CommunicationModels;

namespace RentHub.Portal.ViewModels.PlanInquiries
{
    public class PlanInquiryVm
    {
        [Required, StringLength(80)] public string PlanName { get; set; } = "Enterprise Unlimited";
        [Range(1, 100000)] public int PropertyCount { get; set; } = 1;
        [Range(1, 1000000)] public int ApartmentCount { get; set; } = 1;
        [Range(1, 1000000)] public int TenantCount { get; set; } = 1;
        [Range(typeof(decimal), "0.01", "1000000000")] public decimal ProposedMonthlyPrice { get; set; }
        [StringLength(160)] public string? RequesterName { get; set; }
        [EmailAddress, StringLength(256)] public string? RequesterEmail { get; set; }
        [Required, StringLength(3000, MinimumLength = 2)] public string Message { get; set; } = string.Empty;
        public bool IsAuthenticated { get; set; }
    }

    public class PublicPlanInquiryThreadVm
    {
        public string Token { get; set; } = string.Empty;
        public SubscriptionInquiryThreadDto Thread { get; set; } = new();
    }
}
