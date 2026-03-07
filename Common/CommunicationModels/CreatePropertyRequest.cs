using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    /// <summary>
    /// DTO used when creating a property.
    /// </summary>
    public class CreatePropertyRequest
    {
        [Required]
        public string Name { get; set; } = string.Empty;

        [Required]
        public string City { get; set; } = string.Empty;

        [Required]
        public string Address { get; set; } = string.Empty;

        public string? Description { get; set; }

        // Optional. Used by Admin/Manager scenarios where creation targets a landlord account.
        public string? LandlordId { get; set; }
    }
}
