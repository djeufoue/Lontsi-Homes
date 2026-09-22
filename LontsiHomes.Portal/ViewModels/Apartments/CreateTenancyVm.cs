using Microsoft.AspNetCore.Http;
using System.ComponentModel.DataAnnotations;

namespace LontsiHomes.Portal.ViewModels.Apartments
{
    public class CreateTenancyVm
    {
        [Required]
        public int ApartmentId { get; set; }

        [Required]
        public DateTimeOffset StartDate { get; set; }

        public DateTimeOffset? EndDate { get; set; }

        [Required]
        [Range(0, double.MaxValue)]
        public decimal MonthlyRent { get; set; }

        [Range(1, int.MaxValue)]
        public int MaxMembers { get; set; } = 1;

        public IFormFile? ContractDocument { get; set; }
    }
}
