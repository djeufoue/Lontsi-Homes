using System.Text.Json;
using Common.Helpers;

namespace RentHub.API.Services.Messaging;

public static class WhatsAppInboundPolicy
{
    private static readonly HashSet<string> UnsubscribeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "STOP", "ARRÊT", "ARRET", "DÉSABONNER", "DESABONNER", "UNSUBSCRIBE"
    };

    public static bool IsUnsubscribe(string? text) =>
        !string.IsNullOrWhiteSpace(text) && UnsubscribeWords.Contains(text.Trim());

    public static bool TryGetUnsubscribeSender(JsonElement item, out string senderNumber)
    {
        senderNumber = string.Empty;
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // Keep the text and sender on the same event: Infobip nests text under message.
        var text = NestedText(item, "message") ?? NestedText(item, "content") ?? StringValue(item, "text");
        var from = StringValue(item, "from");
        if (!IsUnsubscribe(text) || string.IsNullOrWhiteSpace(from))
        {
            return false;
        }

        // Infobip's from is an international number, usually without the leading '+'.
        var internationalNumber = from.Trim();
        if (!internationalNumber.StartsWith('+'))
        {
            internationalNumber = "+" + internationalNumber;
        }
        return PhoneNumberHelper.TryNormalizeE164(null, internationalNumber, out senderNumber);
    }

    private static string? NestedText(JsonElement item, string name) =>
        item.TryGetProperty(name, out var nested) ? StringValue(nested, "text") : null;

    private static string? StringValue(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
