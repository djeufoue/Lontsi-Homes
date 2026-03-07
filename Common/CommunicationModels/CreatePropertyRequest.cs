using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    /// <summary>
    /// DTO used when creating a new property.  This excludes landlord information,
    /// which is derived from the authenticated user.  Apartments are created separately.
    /// </summary>
    public class CreatePropertyRequest
    {
        [Required]
        public string Name { get; set; } = string.Empty;
        [Required]
        public string City { get; set; } = string.Empty;
        [Required]
        public string Address { get; set; } = string.Empty;
    }
}