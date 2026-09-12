using System.Text.Json;
using Common.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Receipts;

namespace RentHub.API.Services.Messaging;

public sealed class NotificationDeliveryService : INotificationDeliveryService
{
    private const int MaximumAttempts = 5;
    private readonly ApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWhatsAppTemplateRegistry _registry;
    private readonly IWhatsAppMessagingService _whatsApp;
    private readonly ILogger<NotificationDeliveryService> _logger;
    private readonly WhatsAppReceiptMedia _receiptMedia;

    public NotificationDeliveryService(
        ApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IWhatsAppTemplateRegistry registry,
        IWhatsAppMessagingService whatsApp,
        ILogger<NotificationDeliveryService> logger,
        WhatsAppReceiptMedia receiptMedia)
    {
        _context = context;
        _userManager = userManager;
        _registry = registry;
        _whatsApp = whatsApp;
        _logger = logger;
        _receiptMedia = receiptMedia;
    }

    public async Task<long?> EnqueueWhatsAppAsync(
        EnqueueWhatsAppNotification notification,
        CancellationToken cancellationToken = default)
    {
        var existing = await _context.NotificationDeliveries
            .AsNoTracking()
            .Where(item => item.IdempotencyKey == notification.IdempotencyKey)
            .Select(item => (long?)item.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (existing.HasValue)
        {
            return existing;
        }

        var user = await _userManager.FindByIdAsync(notification.RecipientUserId);
        if (user == null)
        {
            return null;
        }

        var language = user.EmailLanguage == PlatformLanguage.French ? "fr" : "en";
        if (!_registry.TryGet(notification.EventType, language, out var definition))
        {
            return await RecordSkippedAsync(notification, user, language, "template_not_registered", cancellationToken);
        }

        var hasReceiptReference = notification.ReceiptDocument is { PaymentId: > 0 } reference &&
            !string.IsNullOrWhiteSpace(reference.VerificationCode) && definition.RequiresDocument &&
            notification.EventType == "tenant_rent_payment_received" && notification.Document == null;
        if (notification.BodyPlaceholders.Count != definition.BodyPlaceholderCount ||
            (notification.UrlButtonParameters?.Count ?? 0) != definition.UrlButtonParameterCount ||
            (!hasReceiptReference && !WhatsAppTemplateParameters.AreValid(definition, notification.BodyPlaceholders,
                notification.UrlButtonParameters, notification.Document)))
        {
            return await RecordSkippedAsync(
                notification,
                user,
                language,
                definition.UrlButtonParameterCount > 0 && notification.UrlButtonParameters == null
                    ? "template_url_button_not_configured" : "template_parameters_invalid",
                cancellationToken,
                definition);
        }

        var roles = await _userManager.GetRolesAsync(user);
        if (!roles.Any(role => definition.RecipientRoles.Contains(role)))
        {
            return await RecordSkippedAsync(notification, user, language, "recipient_role_not_allowed", cancellationToken, definition);
        }

        var hasConsent = await HasActiveTransactionalConsentAsync(user, cancellationToken);
        var skipReason = !user.IsWhatsAppPhoneVerified || string.IsNullOrWhiteSpace(user.NormalizedWhatsAppPhoneNumber)
            ? "whatsapp_not_verified"
            : !hasConsent
                ? "transactional_consent_missing"
                : !_registry.IsApproved(definition)
                    ? "template_not_approved"
                    : null;

        var delivery = BuildDelivery(notification, user, language, definition);
        if (skipReason != null)
        {
            delivery.Status = NotificationDeliveryStatuses.Skipped;
            delivery.LastErrorCode = skipReason;
        }

        _context.NotificationDeliveries.Add(delivery);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return delivery.Id;
        }
        catch (DbUpdateException)
        {
            return await _context.NotificationDeliveries
                .AsNoTracking()
                .Where(item => item.IdempotencyKey == notification.IdempotencyKey)
                .Select(item => (long?)item.Id)
                .SingleOrDefaultAsync(cancellationToken);
        }
    }

    public async Task ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        var deliveries = await _context.NotificationDeliveries
            .Where(item => item.Channel == CommunicationChannels.WhatsApp &&
                           (item.Status == NotificationDeliveryStatuses.Pending ||
                            (item.Status == NotificationDeliveryStatuses.Failed && item.AttemptCount < MaximumAttempts)))
            .OrderBy(item => item.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        foreach (var delivery in deliveries)
        {
            await ProcessOneAsync(delivery, cancellationToken);
        }
    }

    private async Task ProcessOneAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        var user = await _context.Users.SingleOrDefaultAsync(item => item.Id == delivery.RecipientUserId, cancellationToken);
        if (user == null ||
            !user.IsWhatsAppPhoneVerified ||
            !string.Equals(user.NormalizedWhatsAppPhoneNumber, delivery.RecipientPhoneNumberE164, StringComparison.Ordinal) ||
            !await HasActiveTransactionalConsentAsync(user, cancellationToken) ||
            !_registry.TryGetByTemplate(delivery.TemplateName, delivery.TemplateLanguage, out var definition) ||
            !_registry.IsApproved(definition))
        {
            delivery.Status = NotificationDeliveryStatuses.Skipped;
            delivery.LastErrorCode = "recipient_no_longer_eligible";
            await _context.SaveChangesAsync(cancellationToken);
            return;
        }

        WhatsAppOutboxPayload payload;
        IReadOnlyList<string> buttons;
        try
        {
            payload = WhatsAppOutboxPayload.Deserialize(delivery.PayloadJson);
            buttons = JsonSerializer.Deserialize<string[]>(delivery.ButtonPayloadsJson) ?? Array.Empty<string>();
        }
        catch (JsonException exception)
        {
            delivery.Status = NotificationDeliveryStatuses.Failed;
            delivery.LastErrorCode = "invalid_outbox_payload";
            delivery.ErrorMessage = exception.Message;
            delivery.FailedAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            return;
        }

        delivery.Status = NotificationDeliveryStatuses.Processing;
        delivery.AttemptCount++;
        delivery.LastAttemptAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        // Create a fresh capability at each attempt, so time spent in the queue does not consume its lifetime.
        var document = payload.ReceiptDocument == null ? payload.Document :
            _receiptMedia.Create(payload.ReceiptDocument, delivery.RecipientUserId, delivery.TemplateLanguage);
        var result = payload.ReceiptDocument != null && document == null
            ? MessagingSendResult.Failure("receipt_media_not_configured", "Configure Infobip:ReceiptMediaBaseUrl with the public HTTPS API URL.")
            : await _whatsApp.SendTemplateAsync(new WhatsAppTemplateMessage(
            delivery.RecipientPhoneNumberE164,
            delivery.TemplateName,
            delivery.TemplateLanguage,
            payload.BodyPlaceholders,
            buttons,
            delivery.IdempotencyKey,
            document), cancellationToken);

        if (result.Succeeded)
        {
            delivery.Status = NotificationDeliveryStatuses.Sent;
            delivery.ProviderMessageId = result.ProviderMessageId;
            delivery.SentAt = DateTimeOffset.UtcNow;
            delivery.LastErrorCode = null;
            delivery.ErrorMessage = null;
        }
        else
        {
            delivery.Status = NotificationDeliveryStatuses.Failed;
            delivery.LastErrorCode = result.ErrorCode;
            delivery.ErrorMessage = result.ErrorMessage;
            delivery.FailedAt = DateTimeOffset.UtcNow;
            _logger.LogWarning("WhatsApp outbox delivery {DeliveryId} failed with {ErrorCode}.", delivery.Id, result.ErrorCode);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> HasActiveTransactionalConsentAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        return await _context.UserCommunicationConsents.AnyAsync(consent =>
            consent.UserId == user.Id &&
            consent.Channel == CommunicationChannels.WhatsApp &&
            consent.Purpose == CommunicationPurposes.Transactional &&
            consent.Status == CommunicationConsentStatuses.Granted &&
            consent.PhoneNumberE164 == user.NormalizedWhatsAppPhoneNumber,
            cancellationToken);
    }

    private NotificationDelivery BuildDelivery(
        EnqueueWhatsAppNotification notification,
        ApplicationUser user,
        string language,
        WhatsAppTemplateDefinition definition)
    {
        return new NotificationDelivery
        {
            Channel = CommunicationChannels.WhatsApp,
            EventType = notification.EventType,
            RelatedEntityId = notification.RelatedEntityId,
            TemplateName = definition.TemplateName,
            TemplateLanguage = definition.LanguageCode,
            RecipientUserId = user.Id,
            RecipientPhoneNumberE164 = user.NormalizedWhatsAppPhoneNumber ?? string.Empty,
            Status = NotificationDeliveryStatuses.Pending,
            IdempotencyKey = notification.IdempotencyKey,
            PayloadJson = WhatsAppOutboxPayload.Serialize(notification.BodyPlaceholders, notification.Document, notification.ReceiptDocument),
            ButtonPayloadsJson = JsonSerializer.Serialize(notification.UrlButtonParameters ?? Array.Empty<string>())
        };
    }

    private async Task<long?> RecordSkippedAsync(
        EnqueueWhatsAppNotification notification,
        ApplicationUser user,
        string language,
        string reason,
        CancellationToken cancellationToken,
        WhatsAppTemplateDefinition? definition = null)
    {
        var delivery = definition == null
            ? new NotificationDelivery
            {
                Channel = CommunicationChannels.WhatsApp,
                EventType = notification.EventType,
                RelatedEntityId = notification.RelatedEntityId,
                TemplateName = string.Empty,
                TemplateLanguage = language,
                RecipientUserId = user.Id,
                RecipientPhoneNumberE164 = user.NormalizedWhatsAppPhoneNumber ?? string.Empty,
                IdempotencyKey = notification.IdempotencyKey,
                PayloadJson = WhatsAppOutboxPayload.Serialize(notification.BodyPlaceholders, notification.Document, notification.ReceiptDocument),
                ButtonPayloadsJson = JsonSerializer.Serialize(notification.UrlButtonParameters ?? Array.Empty<string>())
            }
            : BuildDelivery(notification, user, language, definition);
        delivery.Status = NotificationDeliveryStatuses.Skipped;
        delivery.LastErrorCode = reason;
        _context.NotificationDeliveries.Add(delivery);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return delivery.Id;
        }
        catch (DbUpdateException)
        {
            return await _context.NotificationDeliveries
                .AsNoTracking()
                .Where(item => item.IdempotencyKey == notification.IdempotencyKey)
                .Select(item => (long?)item.Id)
                .SingleOrDefaultAsync(cancellationToken);
        }
    }
}
