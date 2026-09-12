using System.Text.Json;

namespace RentHub.API.Services.Messaging;

/// <summary>Preserves document metadata without changing the outbox database schema.</summary>
public sealed record WhatsAppOutboxPayload(
    IReadOnlyList<string> BodyPlaceholders,
    WhatsAppDocumentHeader? Document = null,
    WhatsAppReceiptReference? ReceiptDocument = null)
{
    public static string Serialize(IReadOnlyList<string> body, WhatsAppDocumentHeader? document,
        WhatsAppReceiptReference? receiptDocument = null) =>
        document == null && receiptDocument == null
            ? JsonSerializer.Serialize(body)
            : JsonSerializer.Serialize(new WhatsAppOutboxPayload(body, document, receiptDocument));

    public static WhatsAppOutboxPayload Deserialize(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        // Existing queued notifications contain a bare string array.
        if (parsed.RootElement.ValueKind == JsonValueKind.Array)
        {
            return new WhatsAppOutboxPayload(JsonSerializer.Deserialize<string[]>(json)!);
        }

        var payload = JsonSerializer.Deserialize<WhatsAppOutboxPayload>(json);
        if (payload?.BodyPlaceholders == null)
        {
            throw new JsonException("Missing WhatsApp body placeholders.");
        }

        return payload;
    }
}
