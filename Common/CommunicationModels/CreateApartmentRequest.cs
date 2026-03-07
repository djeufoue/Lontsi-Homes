using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace Common.CommunicationModels
{
    /// <summary>
    /// DTO used when creating a new apartment.  Only the essential fields are included;
    /// the property and landlord identifiers are resolved at runtime.
    /// </summary>
    public class CreateApartmentRequest
    {
        [Required]
        public int PropertyId { get; set; }
        [Required]
        public string Name { get; set; } = string.Empty;
        [Required]
        public ApartmentTypeEnum Type { get; set; }
        [Range(0, double.MaxValue)]
        public decimal Price { get; set; }
        [Range(0, double.MaxValue)]
        public int Area { get; set; }
    }
}