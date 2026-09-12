using System.ComponentModel.DataAnnotations;

namespace RentHub.API.Models.Entities
{
    public class PlatformPaymentSettings
    {
        [Key]
        public int Id { get; set; } = 1;
        public bool AutomaticPaymentsEnabled { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
