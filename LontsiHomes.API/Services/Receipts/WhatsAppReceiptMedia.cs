using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using LontsiHomes.API.Services.Messaging;

namespace LontsiHomes.API.Services.Receipts;

public sealed record ReceiptMediaGrant(int PaymentId, string VerificationCode, string RecipientUserId,
    string Language, DateTimeOffset ExpiresAt);

/// <summary>Purpose-bound, short-lived bearer capability for a single tenant receipt.</summary>
public sealed class WhatsAppReceiptMedia
{
    private readonly IDataProtector _protector;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _clock;

    public WhatsAppReceiptMedia(IDataProtectionProvider provider, IConfiguration configuration, TimeProvider clock)
    {
        _protector = provider.CreateProtector("LontsiHomes.WhatsAppReceiptMedia.v1");
        _configuration = configuration;
        _clock = clock;
    }

    public WhatsAppDocumentHeader? Create(WhatsAppReceiptReference receipt, string recipientUserId, string language)
    {
        var publicUrl = _configuration["Infobip:ReceiptMediaBaseUrl"];
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return null;

        var grant = new ReceiptMediaGrant(receipt.PaymentId, receipt.VerificationCode, recipientUserId,
            language, _clock.GetUtcNow().AddHours(24));
        var token = _protector.Protect(JsonSerializer.Serialize(grant));
        return new WhatsAppDocumentHeader(
            $"{uri.AbsoluteUri.TrimEnd('/')}/api/receipts/whatsapp.pdf?token={Uri.EscapeDataString(token)}",
            $"receipt-{receipt.PaymentId}.pdf");
    }

    public ReceiptMediaGrant? Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 8192) return null;
        try
        {
            var grant = JsonSerializer.Deserialize<ReceiptMediaGrant>(_protector.Unprotect(token));
            return grant != null && grant.PaymentId > 0 && !string.IsNullOrWhiteSpace(grant.VerificationCode) &&
                   !string.IsNullOrWhiteSpace(grant.RecipientUserId) && grant.Language is "fr" or "en" &&
                   grant.ExpiresAt > _clock.GetUtcNow() ? grant : null;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
    }
}
