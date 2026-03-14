using System.ComponentModel.DataAnnotations;

namespace RentHub.Portal.ViewModels.Properties
{
    public class CreatePropertyVm
    {
        [Required]
        public string Name { get; set; } = string.Empty;

        [Required]
        public string City { get; set; } = string.Empty;

        [Required]
        public string Address { get; set; } = string.Empty;

        public string? Description { get; set; }

        [Range(-90, 90)]
        public double? Latitude { get; set; }

        [Range(-180, 180)]
        public double? Longitude { get; set; }

        // For manager/admin create scope.
        public string? LandlordId { get; set; }
    }
}
