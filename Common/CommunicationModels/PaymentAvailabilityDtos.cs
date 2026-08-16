namespace Common.CommunicationModels
{
    public class PaymentAvailabilityDto
    {
        public bool PlatformAutomaticPaymentsEnabled { get; set; }
        public int? PropertyId { get; set; }
        public bool? PropertyAutomaticPaymentsEnabled { get; set; }
        public bool EffectiveAutomaticPaymentsEnabled { get; set; }
        public bool SkipLandlordPhoneVerification { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class UpdateAutomaticPaymentAvailabilityRequest
    {
        public bool Enabled { get; set; }
    }

    public class UpdateLandlordPhoneVerificationBypassRequest
    {
        public bool Enabled { get; set; }
    }
}
