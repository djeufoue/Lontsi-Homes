using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace Common.CommunicationModels
{
    public class UpdateApartmentRequest
    {
        [Required, MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        [Required]
        public ApartmentTypeEnum Type { get; set; }

        [Range(0, double.MaxValue)]
        public decimal Price { get; set; }

        [Range(0, int.MaxValue)]
        public int Area { get; set; }

        [Range(0, 30)]
        public int FloorNumber { get; set; }
    }
}
