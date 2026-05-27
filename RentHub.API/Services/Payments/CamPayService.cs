using System.Globalization;
using System.Text;
using System.Text.Json;
using Common.Enums;
using RentHub.API.Models.Entities;

namespace RentHub.API.Services.Payments
{
    public interface ICamPayService
    {
        Task<CamPayCollectResult> InitializeSubscriptionCheckoutAsync(
            ApplicationUser user,
            SubscriptionPlan plan,
            UserSubscription subscription,
            PaymentMethodEnum paymentMethod,
            string mobileMoneyPhoneNumber);

        Task<CamPayPaymentStatus?> RetrievePaymentAsync(string providerReference);
    }

    public sealed class CamPayCollectResult
    {
        public string ProviderReference { get; init; } = string.Empty;
        public string Status { get; init; } = "PENDING";
        public string Operator { get; init; } = string.Empty;
        public string UssdCode { get; init; } = string.Empty;
    }

    public sealed class CamPayPaymentStatus
    {
        public string ProviderReference { get; init; } = string.Empty;
        public string ExternalReference { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Operator { get; init; } = string.Empty;
        public string? OperatorReference { get; init; }
    }

    public class CamPayService : ICamPayService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<CamPayService> _logger;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);
        private string? _accessToken;
        private DateTimeOffset _accessTokenExpiresAt;

        public CamPayService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<CamPayService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<CamPayCollectResult> InitializeSubscriptionCheckoutAsync(
            ApplicationUser user,
            SubscriptionPlan plan,
            UserSubscription subscription,
            PaymentMethodEnum paymentMethod,
            string mobileMoneyPhoneNumber)
        {
            var phoneNumber = NormalizeCameroonPhoneNumber(mobileMoneyPhoneNumber);
            if (string.IsNullOrWhiteSpace(phoneNumber))
            {
                throw new InvalidOperationException("A valid Cameroon Mobile Money phone number is required.");
            }

            var payload = new Dictionary<string, object?>
            {
                ["amount"] = decimal.ToInt32(decimal.Round(plan.Price, 0, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture),
                ["currency"] = "XAF",
                ["from"] = phoneNumber,
                ["description"] = $"Lontsi Homes subscription - {plan.Name}",
                ["external_reference"] = subscription.PaymentReference
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("collect/"));
            request.Headers.Add("Authorization", $"Token {await GetAccessTokenAsync()}");
            request.Content = JsonContent(payload);

            using var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request);
            var raw = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("CamPay collection initialization failed with status {StatusCode}: {Payload}", response.StatusCode, raw);
                throw new InvalidOperationException("Unable to initialize the Mobile Money payment right now.");
            }

            using var document = JsonDocument.Parse(raw);
            var providerReference = FindString(document.RootElement, "reference");
            if (string.IsNullOrWhiteSpace(providerReference))
            {
                throw new InvalidOperationException("CamPay did not return a payment reference.");
            }

            return new CamPayCollectResult
            {
                ProviderReference = providerReference,
                Status = FindString(document.RootElement, "status") ?? "PENDING",
                Operator = FindString(document.RootElement, "operator") ?? ResolveExpectedOperator(paymentMethod),
                UssdCode = FindString(document.RootElement, "ussd_code") ?? string.Empty
            };
        }

        public async Task<CamPayPaymentStatus?> RetrievePaymentAsync(string providerReference)
        {
            if (string.IsNullOrWhiteSpace(providerReference))
            {
                return null;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                BuildUri($"transaction/{Uri.EscapeDataString(providerReference)}/"));
            request.Headers.Add("Authorization", $"Token {await GetAccessTokenAsync()}");

            using var client = _httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request);
            var raw = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("CamPay payment lookup failed for provider reference {Reference}: {Payload}", providerReference, raw);
                return null;
            }

            using var document = JsonDocument.Parse(raw);
            return new CamPayPaymentStatus
            {
                ProviderReference = FindString(document.RootElement, "reference") ?? providerReference,
                ExternalReference = FindString(document.RootElement, "external_reference") ?? string.Empty,
                Status = FindString(document.RootElement, "status") ?? "PENDING",
                Operator = FindString(document.RootElement, "operator") ?? string.Empty,
                OperatorReference = FindString(document.RootElement, "operator_reference")
            };
        }

        private async Task<string> GetAccessTokenAsync()
        {
            if (!string.IsNullOrWhiteSpace(_accessToken) &&
                _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                return _accessToken;
            }

            await _tokenLock.WaitAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(_accessToken) &&
                    _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
                {
                    return _accessToken;
                }

                var username = _configuration["CamPay:AppUsername"]?.Trim();
                var password = _configuration["CamPay:AppPassword"]?.Trim();
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    throw new InvalidOperationException("CamPay API credentials are missing.");
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("token/"));
                request.Content = JsonContent(new Dictionary<string, object?>
                {
                    ["username"] = username,
                    ["password"] = password
                });

                using var client = _httpClientFactory.CreateClient();
                using var response = await client.SendAsync(request);
                var raw = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("CamPay token request failed with status {StatusCode}: {Payload}", response.StatusCode, raw);
                    throw new InvalidOperationException("Unable to authenticate with CamPay.");
                }

                using var document = JsonDocument.Parse(raw);
                var token = FindString(document.RootElement, "token");
                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new InvalidOperationException("CamPay did not return an access token.");
                }

                _accessToken = token;
                var expiresIn = FindInt(document.RootElement, "expires_in") ?? 3600;
                _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn));

                return _accessToken;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        private Uri BuildUri(string path)
        {
            var baseUrl = (_configuration["CamPay:BaseUrl"] ?? "https://demo.campay.net/api").Trim().TrimEnd('/');
            return new Uri($"{baseUrl}/{path.TrimStart('/')}");
        }

        private static StringContent JsonContent<T>(T payload)
        {
            return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }

        private static string? NormalizeCameroonPhoneNumber(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var digits = new string(value.Where(char.IsDigit).ToArray());
            if (digits.StartsWith("00", StringComparison.Ordinal))
            {
                digits = digits[2..];
            }

            if (digits.Length == 9 && digits.StartsWith("6", StringComparison.Ordinal))
            {
                return $"237{digits}";
            }

            if (digits.Length == 12 && digits.StartsWith("2376", StringComparison.Ordinal))
            {
                return digits;
            }

            return null;
        }

        private static string ResolveExpectedOperator(PaymentMethodEnum paymentMethod)
        {
            return paymentMethod switch
            {
                PaymentMethodEnum.Momo => "MTN",
                PaymentMethodEnum.OrangeMoney => "ORANGE",
                _ => string.Empty
            };
        }

        private static int? FindInt(JsonElement element, string propertyName)
        {
            var value = FindString(element, propertyName);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
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
