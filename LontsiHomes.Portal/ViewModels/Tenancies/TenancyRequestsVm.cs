using System.ComponentModel.DataAnnotations;
using Common.CommunicationModels;

namespace LontsiHomes.Portal.ViewModels.Tenancies
{
    public class TenancyRequestsVm
    {
        public TenancyRequestListDto Requests { get; set; } = new();
        public TenancyDetailsDto? SelectedTenancy { get; set; }
        public TenancyRenewalWorkspaceDto? RenewalWorkspace { get; set; }
        public string? RequestType { get; set; }
        public string? Status { get; set; }
        public int? PropertyId { get; set; }
        public int? ApartmentId { get; set; }
        public int PageSize { get; set; } = 12;
        public bool OpenRequestForm { get; set; }

        [DataType(DataType.Date)]
        public DateTime? RequestedEndDate { get; set; }

        [MaxLength(1000)]
        public string? TerminationReason { get; set; }

        [DataType(DataType.Date)]
        public DateTime? ProposedEndDate { get; set; }
    }
}
