using System;
using Common.Enums;

namespace Common.CommunicationModels
{
    public class SystemTransferAccountDto
    {
        public int Id { get; set; }
        public PayoutChannelEnum Channel { get; set; }
        public string ChannelLabel { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string PhoneNumber { get; set; } = string.Empty;
        public string CountryCode { get; set; } = "+237";
        public string? Notes { get; set; }
        public bool IsConfigured { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
    }
}
