using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace RentHub.Portal.ViewModels.Properties
{
    public class CreateApartmentVm
    {
        [Required]
        public int PropertyId { get; set; }

        [Required]
        public string Name { get; set; } = string.Empty;

        [Required]
        public ApartmentTypeEnum Type { get; set; }

        [Range(0, double.MaxValue)]
        public decimal Price { get; set; }

        [Range(0, int.MaxValue)]
        public int Area { get; set; }
    }
}
