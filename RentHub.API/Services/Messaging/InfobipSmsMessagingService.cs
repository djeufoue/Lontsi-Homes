using System.Net.Http.Json;
using System.Text.Json;
using Common.Helpers;
using Microsoft.Extensions.Options;
using RentHub.API.Models.Settings;

namespace RentHub.API.Services.Messaging;

public sealed class InfobipSmsMessagingService : ISmsMessagingService
{
    private readonly HttpClient _httpClient;
    private readonly InfobipOptions _options;
    private readonly ILogger<InfobipSmsMessagingService> _logger;

    public InfobipSmsMessagingService(
        HttpClient httpClient,
        IOptions<InfobipOptions> options,
        ILogger<InfobipSmsMessagingService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<MessagingSendResult> SendAsync(
        string recipientPhoneNumberE164,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.SmsSender))
        {
            return MessagingSendResult.Failure("provider_not_configured", "Infobip SMS is not configured.");
        }

        if (!PhoneNumberHelper.TryNormalizeE164(null, recipientPhoneNumberE164, out var recipient))
        {
            return MessagingSendResult.Failure("invalid_recipient", "The SMS recipient must be a valid E.164 number.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "sms/3/messages")
        {
            Content = JsonContent.Create(new
            {
                messages = new[]
                {
                    new
                    {
                        sender = _options.SmsSender,
                        destinations = new[] { new { to = PhoneNumberHelper.ToProviderDigits(recipient) } },
                        content = new { text = message }
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
                _logger.LogWarning("Infobip SMS rejected the request with status {StatusCode}.", response.StatusCode);
                return MessagingSendResult.Failure("provider_rejected", json);
            }

            using var document = JsonDocument.Parse(json);
            var id = document.RootElement.TryGetProperty("messages", out var messages) &&
                     messages.ValueKind == JsonValueKind.Array && messages.GetArrayLength() > 0 &&
                     messages[0].TryGetProperty("messageId", out var messageId)
                ? messageId.GetString()
                : null;
            return MessagingSendResult.Success(id);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(exception, "Infobip SMS delivery failed.");
            return MessagingSendResult.Failure("provider_error", exception.Message);
        }
    }
}
