using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LontsiHomes.API.Data;
using LontsiHomes.API.Models.Entities;
using LontsiHomes.API.Models.Settings;
using LontsiHomes.API.Services.Messaging;

namespace LontsiHomes.API.Controllers;

[ApiController]
[Route("api/webhooks/infobip")]
[AllowAnonymous]
public sealed class InfobipWebhooksController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly InfobipOptions _options;
    private readonly ILogger<InfobipWebhooksController> _logger;

    public InfobipWebhooksController(
        ApplicationDbContext context,
        IOptions<InfobipOptions> options,
        ILogger<InfobipWebhooksController> logger)
    {
        _context = context;
        _options = options.Value;
        _logger = logger;
    }

    [HttpPost("whatsapp")]
    public async Task<IActionResult> WhatsApp([FromBody] JsonElement payload, CancellationToken cancellationToken)
    {
        if (!IsAuthorized())
        {
            return Unauthorized();
        }

        foreach (var item in EnumerateObjects(payload))
        {
            await ApplyDeliveryStatusAsync(item, cancellationToken);
            await ApplyStopAsync(item, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return Ok();
    }

    private bool IsAuthorized()
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            _logger.LogWarning("Rejected Infobip webhook because no webhook secret is configured.");
            return false;
        }

        var supplied = Request.Headers["X-Infobip-Webhook-Secret"].FirstOrDefault() ??
                       Request.Query["secret"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(supplied))
        {
            return false;
        }

        var actual = Encoding.UTF8.GetBytes(supplied);
        var expected = Encoding.UTF8.GetBytes(_options.WebhookSecret);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private async Task ApplyDeliveryStatusAsync(JsonElement item, CancellationToken cancellationToken)
    {
        if (!TryString(item, "messageId", out var messageId) || string.IsNullOrWhiteSpace(messageId))
        {
            return;
        }

        var status = TryNestedString(item, "status", "groupName") ??
                     TryNestedString(item, "status", "name") ??
                     (TryString(item, "status", out var directStatus) ? directStatus : null);
        if (string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        var delivery = await _context.NotificationDeliveries
            .SingleOrDefaultAsync(candidate => candidate.ProviderMessageId == messageId, cancellationToken);
        if (delivery == null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (status.Contains("DELIVER", StringComparison.OrdinalIgnoreCase))
        {
            delivery.Status = NotificationDeliveryStatuses.Delivered;
            delivery.DeliveredAt ??= now;
        }
        else if (status.Contains("READ", StringComparison.OrdinalIgnoreCase))
        {
            delivery.Status = NotificationDeliveryStatuses.Read;
            delivery.ReadAt ??= now;
            delivery.DeliveredAt ??= now;
        }
        else if (status.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
                 status.Contains("REJECT", StringComparison.OrdinalIgnoreCase) ||
                 status.Contains("UNDELIVER", StringComparison.OrdinalIgnoreCase))
        {
            delivery.Status = NotificationDeliveryStatuses.Failed;
            delivery.FailedAt ??= now;
            delivery.LastErrorCode = status;
        }
        else if (status.Contains("SENT", StringComparison.OrdinalIgnoreCase))
        {
            delivery.Status = NotificationDeliveryStatuses.Sent;
            delivery.SentAt ??= now;
        }
    }

    private async Task ApplyStopAsync(JsonElement item, CancellationToken cancellationToken)
    {
        if (!WhatsAppInboundPolicy.TryGetUnsubscribeSender(item, out var senderNumber))
        {
            return; // Ordinary inbound messages are intentionally ignored.
        }

        var user = await _context.Users.SingleOrDefaultAsync(candidate =>
            candidate.NormalizedWhatsAppPhoneNumber == senderNumber && candidate.IsWhatsAppPhoneVerified,
            cancellationToken);
        if (user == null)
        {
            return;
        }

        var activeConsents = await _context.UserCommunicationConsents
            .Where(consent => consent.UserId == user.Id &&
                              consent.Channel == CommunicationChannels.WhatsApp &&
                              consent.Purpose == CommunicationPurposes.Transactional &&
                              consent.Status == CommunicationConsentStatuses.Granted)
            .ToListAsync(cancellationToken);
        foreach (var consent in activeConsents)
        {
            consent.Status = CommunicationConsentStatuses.Revoked;
            consent.RevokedAt = DateTimeOffset.UtcNow;
        }
    }

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                foreach (var descendant in EnumerateObjects(child))
                {
                    yield return descendant;
                }
            }
            yield break;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        yield return element;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                foreach (var descendant in EnumerateObjects(property.Value))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value = property.GetString() ?? string.Empty);
    }

    private static string? TryNestedString(JsonElement element, string parent, string child)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(parent, out var parentElement) &&
               TryString(parentElement, child, out var value)
            ? value
            : null;
    }
}
