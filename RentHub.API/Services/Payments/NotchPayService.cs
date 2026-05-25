using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Enums;
using RentHub.API.Models.Entities;

namespace RentHub.API.Services.Payments
{
    public interface INotchPayService
    {
        Task<NotchPayCheckoutResult> InitializeSubscriptionCheckoutAsync(
            ApplicationUser user,
            SubscriptionPlan plan,
            UserSubscription subscription,
            PaymentMethodEnum paymentMethod,
            bool allowAutomaticCardPayments);

        Task<NotchPayPaymentStatus?> RetrievePaymentAsync(string reference);

        bool VerifyWebhookSignature(string rawPayload, string? signatureHeader);
    }

    public sealed class NotchPayCheckoutResult
    {
        public string AuthorizationUrl { get; init; } = string.Empty;
        public string? ProviderPaymentId { get; init; }
        public string Status { get; init; } = string.Empty;
    }

    public sealed class NotchPayPaymentStatus
    {
        public string Reference { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string? ProviderPaymentId { get; init; }
        public string? ProviderTransactionId { get; init; }
    }

    public class NotchPayService : INotchPayService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<NotchPayService> _logger;

        public NotchPayService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<NotchPayService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<NotchPayCheckoutResult> InitializeSubscriptionCheckoutAsync(
            ApplicationUser user,
            SubscriptionPlan plan,
            UserSubscription subscription,
            PaymentMethodEnum paymentMethod,
            bool allowAutomaticCardPayments)
        {
            var apiKey = _configuration["NotchPay:ApiKey"]?.Trim();
            var baseUrl = (_configuration["NotchPay:BaseUrl"] ?? "https://api.notchpay.co").Trim().TrimEnd('/');
            var portalBaseUrl = _configuration["Portal:BaseUrl"]?.Trim().TrimEnd('/');

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("Notch Pay API key is missing.");
            }

            if (string.IsNullOrWhiteSpace(portalBaseUrl))
            {
                throw new InvalidOperationException("Portal base URL is missing.");
            }

            var customerPhone = BuildInternationalPhoneNumber(user.CountryCode, user.PhoneNumber)
                ?? BuildInternationalPhoneNumber(user.CountryCode, user.PayoutPhoneNumber)
                ?? BuildInternationalPhoneNumber("+237", user.PhoneNumber)
                ?? BuildInternationalPhoneNumber("+237", user.PayoutPhoneNumber);

            var payload = new Dictionary<string, object?>
            {
                ["amount"] = decimal.ToInt32(decimal.Round(plan.Price, 0, MidpointRounding.AwayFromZero)),
                ["currency"] = "XAF",
                ["reference"] = subscription.PaymentReference,
                ["callback"] = $"{portalBaseUrl}/Profile/SubscriptionCallback?reference={Uri.EscapeDataString(subscription.PaymentReference)}",
                ["description"] = $"Lontsi Homes subscription - {plan.Name}",
                ["locked_currency"] = "XAF",
                ["locked_country"] = "CM",
                ["email"] = user.Email,
                ["phone"] = customerPhone,
                ["customer_meta"] = new Dictionary<string, object?>
                {
                    ["name"] = user.FullName,
                    ["purpose"] = "landlord_subscription",
                    ["subscriptionId"] = subscription.Id,
                    ["planId"] = plan.Id,
                    ["userId"] = user.Id,
                    ["paymentMethod"] = paymentMethod.ToString(),
                    ["allowAutomaticCardPayments"] = allowAutomaticCardPayments
                }
            };

            var lockedChannel = paymentMethod switch
            {
                PaymentMethodEnum.Momo => "cm.mtn",
                PaymentMethodEnum.OrangeMoney => "cm.orange",
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(lockedChannel))
            {
                payload["locked_channel"] = lockedChannel;
            }

            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/payments");
            request.Headers.Add("Authorization", apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request);
            var raw = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Notch Pay checkout initialization failed with status {StatusCode}: {Payload}", response.StatusCode, raw);
                throw new InvalidOperationException("Unable to initialize the subscription payment right now.");
            }

            using var document = JsonDocument.Parse(raw);
            var authorizationUrl = FindString(document.RootElement, "authorization_url")
                ?? FindString(document.RootElement, "checkout_url")
                ?? FindString(document.RootElement, "payment_url");

            if (string.IsNullOrWhiteSpace(authorizationUrl))
            {
                throw new InvalidOperationException("Notch Pay did not return a checkout URL.");
            }

            return new NotchPayCheckoutResult
            {
                AuthorizationUrl = authorizationUrl,
                ProviderPaymentId = FindString(document.RootElement, "id"),
                Status = FindString(document.RootElement, "status") ?? "pending"
            };
        }

        public async Task<NotchPayPaymentStatus?> RetrievePaymentAsync(string reference)
        {
            var apiKey = _configuration["NotchPay:ApiKey"]?.Trim();
            var baseUrl = (_configuration["NotchPay:BaseUrl"] ?? "https://api.notchpay.co").Trim().TrimEnd('/');

            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(reference))
            {
                return null;
            }

            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/payments/{Uri.EscapeDataString(reference)}");
            request.Headers.Add("Authorization", apiKey);

            using var response = await client.SendAsync(request);
            var raw = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Notch Pay payment lookup failed for reference {Reference}: {Payload}", reference, raw);
                return null;
            }

            using var document = JsonDocument.Parse(raw);

            return new NotchPayPaymentStatus
            {
                Reference = FindString(document.RootElement, "reference") ?? reference,
                Status = FindString(document.RootElement, "status")
                    ?? FindString(document.RootElement, "payment_status")
                    ?? "unknown",
                ProviderPaymentId = FindString(document.RootElement, "id"),
                ProviderTransactionId = FindString(document.RootElement, "trxref")
                    ?? FindString(document.RootElement, "transaction_id")
            };
        }

        public bool VerifyWebhookSignature(string rawPayload, string? signatureHeader)
        {
            var secret = _configuration["NotchPay:WebhookSecret"]?.Trim();
            if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signatureHeader))
            {
                return false;
            }

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawPayload));
            var expected = Convert.ToHexString(hash).ToLowerInvariant();
            var provided = signatureHeader.Trim().ToLowerInvariant();

            return FixedTimeEquals(expected, provided);
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            var leftBytes = Encoding.UTF8.GetBytes(left);
            var rightBytes = Encoding.UTF8.GetBytes(right);

            if (leftBytes.Length != rightBytes.Length)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }

        private static string? BuildInternationalPhoneNumber(string? countryCode, string? number)
        {
            if (string.IsNullOrWhiteSpace(number))
            {
                return null;
            }

            var normalizedNumber = number.Trim();
            if (normalizedNumber.StartsWith("+", StringComparison.Ordinal))
            {
                return normalizedNumber;
            }

            var normalizedCountryCode = string.IsNullOrWhiteSpace(countryCode) ? string.Empty : countryCode.Trim();
            if (string.IsNullOrWhiteSpace(normalizedCountryCode))
            {
                return normalizedNumber;
            }

            return $"{normalizedCountryCode.TrimEnd()}${normalizedNumber}".Replace("$", string.Empty, StringComparison.Ordinal);
        }

        private static string? FindString(JsonElement element, string propertyName)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        return property.Value.ValueKind switch
                        {
                            JsonValueKind.String => property.Value.GetString(),
                            JsonValueKind.Number => property.Value.ToString(),
                            _ => null
                        };
                    }

                    var nested = FindString(property.Value, propertyName);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindString(item, propertyName);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
            }

            return null;
        }
    }
}
