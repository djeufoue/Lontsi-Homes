using System.ComponentModel.DataAnnotations;
using Common.CommunicationModels;

namespace RentHub.Portal.ViewModels.Tenancies
{
    public class TenancyRenewalVm
    {
        public TenancyRenewalWorkspaceDto Workspace { get; set; } = new();

        [Required(ErrorMessage = "Choose a proposed end date.")]
        [DataType(DataType.Date)]
        public DateTime? ProposedEndDate { get; set; }

        [MaxLength(512, ErrorMessage = "The rejection reason cannot exceed 512 characters.")]
        public string? RejectionReason { get; set; }
    }
}
