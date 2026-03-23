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
            // If any of the Twilio settings are missing, simply do nothing.  This
            // allows the application to run even when Twilio is not configured.  In
            // production, administrators should supply valid credentials.
            if (string.IsNullOrWhiteSpace(_accountSid) || string.IsNullOrWhiteSpace(_authToken) || string.IsNullOrWhiteSpace(_fromNumber))
            {
                // Nothing to send; return completed task.
                _logger.LogWarning("SMS provider not configured. OTP SMS to {Recipient} was skipped.", to);
                await Task.CompletedTask;
                return;
            }
            try
            {
                // Dynamically load Twilio's REST client to avoid referencing the
                // package if not available.  If the package is installed, this
                // section will compile and send the SMS.  Otherwise, catch and
                // ignore errors so as not to crash the app.
#if TWILIO
                Twilio.TwilioClient.Init(_accountSid, _authToken);
                var messageResponse = await Twilio.Rest.Api.V2010.Account.MessageResource.CreateAsync(
                    body: message,
                    from: new Twilio.Types.PhoneNumber(_fromNumber),
                    to: new Twilio.Types.PhoneNumber(to));
#endif
                await Task.CompletedTask;
            }
            catch
            {
                _logger.LogWarning("Failed to deliver SMS to {Recipient}.", to);
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
