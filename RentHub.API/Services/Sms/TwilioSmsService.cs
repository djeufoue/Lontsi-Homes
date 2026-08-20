using Microsoft.Extensions.Configuration;
using System;
using System.Threading.Tasks;

namespace RentHub.API.Services.Sms
{
    /// <summary>
    /// Implementation of <see cref="ISmsService"/> that integrates with Twilio to
    /// send SMS messages.  Twilio credentials should be configured in
    /// appsettings.json under the "Twilio" section.  This class is intentionally
    /// lightweight; full error handling and status callbacks can be added later.
    /// </summary>
    public class TwilioSmsService : ISmsService
    {
        private readonly string _accountSid;
        private readonly string _authToken;
        private readonly string _fromNumber;
        private readonly string _whatsAppFromNumber;
        private readonly ILogger<TwilioSmsService> _logger;

        public TwilioSmsService(IConfiguration configuration, ILogger<TwilioSmsService> logger)
        {
            // Read Twilio settings from configuration.  These keys are optional
            // and can be provided at runtime by the administrator.
            _accountSid = configuration["Twilio:AccountSid"] ?? string.Empty;
            _authToken = configuration["Twilio:AuthToken"] ?? string.Empty;
            _fromNumber = configuration["Twilio:FromNumber"] ?? string.Empty;
            _whatsAppFromNumber = configuration["Twilio:WhatsAppFromNumber"] ?? string.Empty;
            _logger = logger;
        }

        public async Task SendSmsAsync(string to, string message)
        {
            await TrySendSmsAsync(to, message);
        }

        public async Task<SmsSendResult> TrySendSmsAsync(string to, string message)
        {
            if (string.IsNullOrWhiteSpace(_accountSid) || string.IsNullOrWhiteSpace(_authToken) || string.IsNullOrWhiteSpace(_fromNumber))
            {
                const string error = "SMS provider is not configured.";
                _logger.LogWarning("{Error} Message to {Recipient} was skipped.", error, to);
                return SmsSendResult.Failure(error);
            }

            try
            {
#if TWILIO
                Twilio.TwilioClient.Init(_accountSid, _authToken);
                await Twilio.Rest.Api.V2010.Account.MessageResource.CreateAsync(
                    body: message,
                    from: new Twilio.Types.PhoneNumber(_fromNumber),
                    to: new Twilio.Types.PhoneNumber(to));
                return SmsSendResult.Success();
#else
                await Task.CompletedTask;
                const string error = "SMS delivery is not included in this application build.";
                _logger.LogWarning("{Error} Message to {Recipient} was skipped.", error, to);
                return SmsSendResult.Failure(error);
#endif
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to deliver SMS to {Recipient}.", to);
                return SmsSendResult.Failure(exception.Message);
            }
        }

        public async Task SendWhatsAppAsync(string to, string message)
        {
            if (string.IsNullOrWhiteSpace(_accountSid) || string.IsNullOrWhiteSpace(_authToken) || string.IsNullOrWhiteSpace(_whatsAppFromNumber))
            {
                _logger.LogWarning("WhatsApp provider not configured. OTP WhatsApp message to {Recipient} was skipped.", to);
                await Task.CompletedTask;
                return;
            }

            try
            {
#if TWILIO
                Twilio.TwilioClient.Init(_accountSid, _authToken);
                var messageResponse = await Twilio.Rest.Api.V2010.Account.MessageResource.CreateAsync(
                    body: message,
                    from: new Twilio.Types.PhoneNumber($"whatsapp:{_whatsAppFromNumber}"),
                    to: new Twilio.Types.PhoneNumber($"whatsapp:{to}"));
#endif
                await Task.CompletedTask;
            }
            catch
            {
                _logger.LogWarning("Failed to deliver WhatsApp message to {Recipient}.", to);
            }
        }
    }
}
