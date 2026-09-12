namespace RentHub.API.Models.Entities;

public static class CommunicationChannels
{
    public const string Sms = "SMS";
    public const string WhatsApp = "WhatsApp";
    public const string Email = "Email";
}

public static class CommunicationPurposes
{
    public const string Authentication = "Authentication";
    public const string Transactional = "Transactional";
    public const string Marketing = "Marketing";
}

public static class CommunicationConsentStatuses
{
    public const string Granted = "Granted";
    public const string Revoked = "Revoked";
}

public static class NotificationDeliveryStatuses
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Sent = "Sent";
    public const string Delivered = "Delivered";
    public const string Read = "Read";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}
