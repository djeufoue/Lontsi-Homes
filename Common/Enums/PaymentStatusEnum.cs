namespace Common.Enums
{
    /// <summary>
    /// Represents the status of a payment.  Values mirror possible responses from payment providers.
    /// </summary>
    public enum PaymentStatusEnum
    {
        Pending = 0,
        Success = 1,
        Failed = 2,
        Error = 3,
        Unknown = 4
    }
}