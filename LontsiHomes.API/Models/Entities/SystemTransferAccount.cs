using System.ComponentModel.DataAnnotations;
using Common.Enums;

namespace LontsiHomes.API.Models.Entities
{
    public class SystemTransferAccount
    {
        [Key]
        public int Id { get; set; }

        public PayoutChannelEnum Channel { get; set; }

        [MaxLength(160)]
        public string AccountName { get; set; } = string.Empty;

        [MaxLength(40)]
        public string PhoneNumber { get; set; } = string.Empty;

        [MaxLength(8)]
        public string CountryCode { get; set; } = "+237";

        [MaxLength(280)]
        public string? Notes { get; set; }

        public bool IsDeleted { get; set; } = false;
        public string? CreatedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public string? UpdatedBy { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public string? DeletedBy { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
    }
}
