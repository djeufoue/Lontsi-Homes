using System;

namespace LontsiHomes.API.Models.Entities
{
    public class OtpSendLog
    {
        public int Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public ApplicationUser? User { get; set; }
        public string Purpose { get; set; } = string.Empty;
        public string Channel { get; set; } = string.Empty;
        public string Recipient { get; set; } = string.Empty;
        public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
