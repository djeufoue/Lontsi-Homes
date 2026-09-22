using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using RestSharp;
using Common.CommunicationModels;
using LontsiHomes.API.Models.Entities;

namespace LontsiHomes.API.Services.Payments
{
    /// <summary>
    /// Handles Orange Money transactions by interacting with the Orange Money API.  The client
    /// ID and secret must be provided via configuration under the "OrangeSettings" section.
    /// </summary>
    public class OrangeMoneyService : IPaymentService
    {
        private readonly string _baseUrl = "https://api.orange.com";
        private readonly string _clientId;
        private readonly string _clientSecret;

        public OrangeMoneyService(IConfiguration configuration)
        {
            var settings = configuration.GetSection("OrangeSettings").Get<OrangeSettings>();
            _clientId = settings?.ClientId?.Trim() ?? string.Empty;
            _clientSecret = settings?.ClientSecret?.Trim() ?? string.Empty;
        }

        /// <inheritdoc/>
        public async Task<PaymentResult> ProcessPaymentAsync(Payment payment, PaymentRequest request)
        {
            var result = new PaymentResult
            {
                TransactionId = payment.TransactionId
            };
            try
            {
                if (string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_clientSecret))
                {
                    result.Success = false;
                    result.Status = "ERROR";
                    result.ProviderResponse = "Orange Money payment is not configured.";
                    return result;
                }

                string token = await GetAccessToken();
                var client = new RestClient($"{_baseUrl}/orange-money-webpay/dev/v1/transfers");
                var httpRequest = new RestRequest();
                httpRequest.Method = Method.Post;
                httpRequest.AddHeader("Authorization", $"Bearer {token}");
                httpRequest.AddHeader("Content-Type", "application/json");
                var payload = new
                {
                    payer = new { phoneNumber = request.TenantPhoneNumber },
                    payee = new { phoneNumber = request.LandlordPhoneNumber },
                    amount = request.Amount.ToString("F2"),
                    currency = payment.Currency,
                    description = "Rent payment",
                    transactionId = payment.TransactionId
                };
                httpRequest.AddJsonBody(payload);
                var response = await client.ExecuteAsync(httpRequest);
                result.ProviderResponse = response.Content;
                if (!response.IsSuccessful)
                {
                    result.Success = false;
                    result.Status = "FAILED";
                    return result;
                }
                // Immediately check status
                var statusResponse = await GetTransactionStatus(payment.TransactionId);
                result.Success = true;
                result.Status = ParseTransactionStatus(statusResponse);
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Status = "ERROR";
                result.ProviderResponse = ex.Message;
                return result;
            }
        }

        private async Task<string> GetAccessToken()
        {
            var client = new RestClient($"{_baseUrl}/oauth/v3/token");
            var request = new RestRequest();
            request.Method = Method.Post;
            var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}"));
            request.AddHeader("Authorization", $"Basic {credentials}");
            request.AddHeader("Content-Type", "application/x-www-form-urlencoded");
            request.AddParameter("grant_type", "client_credentials");
            var response = await client.ExecuteAsync(request);
            if (!response.IsSuccessful)
            {
                throw new Exception($"Failed to get access token: {response.Content}");
            }
            var tokenResponse = JsonConvert.DeserializeObject<dynamic>(response.Content);
            return tokenResponse.access_token;
        }

        private async Task<string> GetTransactionStatus(string transactionId)
        {
            var token = await GetAccessToken();
            var client = new RestClient($"{_baseUrl}/orange-money-webpay/dev/v1/transactions/{transactionId}");
            var request = new RestRequest();
            request.Method = Method.Get;
            request.AddHeader("Authorization", $"Bearer {token}");
            var response = await client.ExecuteAsync(request);
            if (!response.IsSuccessful)
            {
                throw new Exception($"Failed to get transaction status: {response.Content}");
            }
            return response.Content;
        }

        private static string ParseTransactionStatus(string statusResponse)
        {
            try
            {
                var status = JsonConvert.DeserializeObject<dynamic>(statusResponse);
                return status.status?.ToString() ?? "UNKNOWN";
            }
            catch
            {
                return "UNKNOWN";
            }
        }

        private class OrangeSettings
        {
            public string ClientId { get; set; } = string.Empty;
            public string ClientSecret { get; set; } = string.Empty;
        }
    }
}
