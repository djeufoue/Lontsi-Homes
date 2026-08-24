using System;
using System.Collections.Generic;
using Common.Enums;

namespace Common.CommunicationModels
{
    public class RentReceiptDto
    {
        public int PaymentId { get; set; }
        public int? TenancyId { get; set; }
        public string ReceiptNumber { get; set; } = string.Empty;
        public string VerificationCode { get; set; } = string.Empty;
        public string VerificationUrl { get; set; } = string.Empty;
        public string QrCodeSvg { get; set; } = string.Empty;
        public DateTimeOffset IssuedAt { get; set; }
        public DateTimeOffset PaymentDate { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;
        public PaymentMethodEnum Method { get; set; }
        public PaymentStatusEnum Status { get; set; }
        public string TransactionId { get; set; } = string.Empty;
        public string ProviderReceiptUrl { get; set; } = string.Empty;
        public string TenantName { get; set; } = string.Empty;
        public string TenantEmail { get; set; } = string.Empty;
        public string TenantPhone { get; set; } = string.Empty;
        public PlatformLanguage TenantEmailLanguage { get; set; } = PlatformLanguage.English;
        public string LandlordName { get; set; } = string.Empty;
        public string LandlordEmail { get; set; } = string.Empty;
        public PlatformLanguage LandlordEmailLanguage { get; set; } = PlatformLanguage.English;
        public string PropertyName { get; set; } = string.Empty;
        public string ApartmentName { get; set; } = string.Empty;
        public string PeriodLabel { get; set; } = string.Empty;
        public DateTimeOffset? PeriodStart { get; set; }
        public DateTimeOffset? PeriodEnd { get; set; }
        public List<RentReceiptLineDto> Lines { get; set; } = new();
        public bool IsValid { get; set; } = true;
    }

    public class RentReceiptVerificationDto
    {
        public bool IsValid { get; set; }
        public string ReceiptNumber { get; set; } = string.Empty;
        public DateTimeOffset? IssuedAt { get; set; }
        public DateTimeOffset? PaymentDate { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;
        public PaymentMethodEnum Method { get; set; }
        public string PaymentMethod { get; set; } = string.Empty;
        public string TenantName { get; set; } = string.Empty;
        public string LandlordName { get; set; } = string.Empty;
        public string PropertyName { get; set; } = string.Empty;
        public string ApartmentName { get; set; } = string.Empty;
        public string PeriodLabel { get; set; } = string.Empty;
        public DateTimeOffset? PeriodStart { get; set; }
        public DateTimeOffset? PeriodEnd { get; set; }
        public List<RentReceiptLineDto> Lines { get; set; } = new();
    }

    public class RentReceiptLineDto
    {
        public int RentPeriodId { get; set; }
        public DateTimeOffset PeriodStart { get; set; }
        public DateTimeOffset PeriodEnd { get; set; }
        public decimal PeriodAmount { get; set; }
        public decimal PaidAmount { get; set; }
    }
}
