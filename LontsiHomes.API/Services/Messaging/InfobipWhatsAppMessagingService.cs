using System.Net.Http.Json;
using System.Text.Json;
using Common.Helpers;
using Microsoft.Extensions.Options;
using LontsiHomes.API.Models.Settings;

namespace LontsiHomes.API.Services.Messaging;

public sealed class InfobipWhatsAppMessagingService : IWhatsAppMessagingService
{
    private readonly HttpClient _httpClient;
    private readonly InfobipOptions _options;
    private readonly IWhatsAppTemplateRegistry _registry;
    private readonly ILogger<InfobipWhatsAppMessagingService> _logger;

    public InfobipWhatsAppMessagingService(
        HttpClient httpClient,
        IOptions<InfobipOptions> options,
        IWhatsAppTemplateRegistry registry,
        ILogger<InfobipWhatsAppMessagingService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _registry = registry;
        _logger = logger;
    }

    public async Task<MessagingSendResult> SendTemplateAsync(
        WhatsAppTemplateMessage message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.WhatsAppSender))
        {
            return MessagingSendResult.Failure("provider_not_configured", "Infobip WhatsApp is not configured.");
        }

        if (!_registry.TryGetByTemplate(message.TemplateName, message.LanguageCode, out var definition) ||
            !_registry.IsApproved(definition))
        {
            return MessagingSendResult.Failure("template_not_approved", "The WhatsApp template is not registered as approved.");
        }

        if (!WhatsAppTemplateParameters.AreValid(definition, message.BodyPlaceholders,
                message.UrlButtonParameters, message.Document))
        {
            return MessagingSendResult.Failure("template_parameters_invalid", "The WhatsApp template parameters do not match its registry definition.");
        }

        if (!PhoneNumberHelper.TryNormalizeE164(null, message.RecipientPhoneNumberE164, out var recipient))
        {
            return MessagingSendResult.Failure("invalid_recipient", "The WhatsApp recipient must be a valid E.164 number.");
        }

        var buttons = (message.UrlButtonParameters ?? Array.Empty<string>())
            .Select(parameter => new { type = "URL", parameter })
            .ToArray();

        var templateData = new Dictionary<string, object>
        {
            ["body"] = new { placeholders = message.BodyPlaceholders }
        };
        if (buttons.Length > 0)
        {
            templateData["buttons"] = buttons;
        }
        if (message.Document != null)
        {
            templateData["header"] = new
            {
                type = "DOCUMENT",
                mediaUrl = message.Document.MediaUrl,
                filename = message.Document.Filename
            };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "whatsapp/1/message/template")
        {
            Content = JsonContent.Create(new
            {
                messages = new[]
                {
                    new
                    {
                        from = PhoneNumberHelper.ToProviderDigits(_options.WhatsAppSender),
                        to = PhoneNumberHelper.ToProviderDigits(recipient),
                        messageId = message.ClientMessageId,
                        content = new
                        {
                            templateName = message.TemplateName,
                            templateData,
                            language = message.LanguageCode
                        }
                    }
                }
            })
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"App {_options.ApiKey}");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Infobip WhatsApp rejected the request with status {StatusCode}.", response.StatusCode);
                return MessagingSendResult.Failure("provider_rejected", json);
            }

            using var document = JsonDocument.Parse(json);
            var id = document.RootElement.TryGetProperty("messages", out var messages) &&
                     messages.ValueKind == JsonValueKind.Array && messages.GetArrayLength() > 0 &&
                     messages[0].TryGetProperty("messageId", out var messageId)
                ? messageId.GetString()
                : message.ClientMessageId;
            return MessagingSendResult.Success(id);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(exception, "Infobip WhatsApp template delivery failed.");
            return MessagingSendResult.Failure("provider_error", exception.Message);
        }
    }
}
