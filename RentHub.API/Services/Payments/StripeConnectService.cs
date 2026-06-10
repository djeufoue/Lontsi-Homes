using System.Net.Http.Headers;
using System.Text.Json;
using RentHub.API.Models.Entities;

namespace RentHub.API.Services.Payments
{
    public interface IStripeConnectService
    {
        Task<StripeConnectAccountStatus> CreateExpressAccountAsync(ApplicationUser user);
        Task<StripeConnectAccountStatus?> RetrieveAccountAsync(string accountId);
        Task<string> CreateOnboardingLinkAsync(string accountId, string returnUrl, string refreshUrl);
        bool IsConnectCountrySupported(string? countryIsoCode);
        string ResolveConnectCountry(ApplicationUser user);
        string GetUnsupportedCountryMessage(string? countryIsoCode);
    }

    public sealed class StripeConnectCountryUnsupportedException : InvalidOperationException
    {
        public StripeConnectCountryUnsupportedException(string message)
            : base(message)
        {
        }
    }

    public sealed class StripeConnectAccountStatus
    {
        public string AccountId { get; init; } = string.Empty;
        public bool DetailsSubmitted { get; init; }
        public bool ChargesEnabled { get; init; }
        public bool PayoutsEnabled { get; init; }
        public string DisabledReason { get; init; } = string.Empty;
        public string RequirementsSummary { get; init; } = string.Empty;
    }

    public class StripeConnectService : IStripeConnectService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<StripeConnectService> _logger;

        public StripeConnectService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<StripeConnectService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<StripeConnectAccountStatus> CreateExpressAccountAsync(ApplicationUser user)
        {
            var country = ResolveConnectCountry(user);
            if (!IsConnectCountrySupported(country))
            {
                throw new StripeConnectCountryUnsupportedException(GetUnsupportedCountryMessage(country));
            }

            var form = new Dictionary<string, string>
            {
                ["type"] = "express",
                ["country"] = country,
                ["email"] = user.Email ?? string.Empty,
                ["capabilities[card_payments][requested]"] = "true",
                ["capabilities[transfers][requested]"] = "true"
            };

            var businessProfileUrl = _configuration["Stripe:Connect:BusinessProfileUrl"]?.Trim();
            if (!string.IsNullOrWhiteSpace(businessProfileUrl))
            {
                form["business_profile[url]"] = businessProfileUrl;
            }

            using var response = await SendStripeFormAsync(HttpMethod.Post, "accounts", form);
            var raw = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Stripe Connect account creation failed with status {StatusCode}: {Payload}", response.StatusCode, raw);
                throw new InvalidOperationException(ExtractStripeMessage(raw));
            }

            using var document = JsonDocument.Parse(raw);
            return ReadAccountStatus(document.RootElement);
        }

        public async Task<StripeConnectAccountStatus?> RetrieveAccountAsync(string accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId))
            {
                return null;
            }

            using var response = await SendStripeFormAsync(
                HttpMethod.Get,
                $"accounts/{Uri.EscapeDataString(accountId)}",
                form: null);

            var raw = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Stripe Connect account lookup failed for {AccountId}: {Payload}", accountId, raw);
                return null;
            }

            using var document = JsonDocument.Parse(raw);
            return ReadAccountStatus(document.RootElement);
        }

        public async Task<string> CreateOnboardingLinkAsync(string accountId, string returnUrl, string refreshUrl)
        {
            if (string.IsNullOrWhiteSpace(accountId))
            {
                throw new InvalidOperationException("Stripe payout account is missing.");
            }

            var form = new Dictionary<string, string>
            {
                ["account"] = accountId,
                ["refresh_url"] = refreshUrl,
                ["return_url"] = returnUrl,
                ["type"] = "account_onboarding"
            };

            using var response = await SendStripeFormAsync(HttpMethod.Post, "account_links", form);
            var raw = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Stripe Connect account link creation failed for {AccountId}: {Payload}", accountId, raw);
                throw new InvalidOperationException(ExtractStripeMessage(raw));
            }

            using var document = JsonDocument.Parse(raw);
            return ReadDirectString(document.RootElement, "url")
                ?? throw new InvalidOperationException("Stripe did not return an onboarding URL.");
        }

        private async Task<HttpResponseMessage> SendStripeFormAsync(
            HttpMethod method,
            string path,
            Dictionary<string, string>? form)
        {
            var secretKey = _configuration["Stripe:SecretKey"]?.Trim();
            if (string.IsNullOrWhiteSpace(secretKey))
            {
                throw new InvalidOperationException("Stripe secret key is missing.");
            }

            var baseUrl = (_configuration["Stripe:BaseUrl"] ?? "https://api.stripe.com").Trim().TrimEnd('/');
            using var request = new HttpRequestMessage(method, $"{baseUrl}/v1/{path.TrimStart('/')}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
            if (method != HttpMethod.Get && form != null)
            {
                request.Content = new FormUrlEncodedContent(form);
            }

            var client = _httpClientFactory.CreateClient();
            return await client.SendAsync(request);
        }

        public string ResolveConnectCountry(ApplicationUser user)
        {
            var country = NormalizeCountryIso(user.CountryIsoCode)
                ?? ResolveCountryIsoFromPhoneCode(user.CountryCode)
                ?? _configuration["Stripe:Connect:DefaultCountry"]
                ?? _configuration["Stripe:DefaultCountry"]
                ?? "CA";

            return NormalizeCountryIso(country) ?? "CA";
        }

        public bool IsConnectCountrySupported(string? countryIsoCode)
        {
            var country = NormalizeCountryIso(countryIsoCode);
            if (string.IsNullOrWhiteSpace(country))
            {
                return false;
            }

            var configuredCountries = _configuration
                .GetSection("Stripe:Connect:SupportedCountries")
                .Get<string[]>()?
                .Select(NormalizeCountryIso)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            configuredCountries ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CA", "US", "GB", "FR", "BE", "DE"
            };

            return configuredCountries.Contains(country);
        }

        public string GetUnsupportedCountryMessage(string? countryIsoCode)
        {
            var country = NormalizeCountryIso(countryIsoCode) ?? "this country";
            return $"Stripe Connect payouts are not available for {country} in this setup. Use the alternate payout path for this landlord before collecting tenant rent.";
        }

        private string ResolveConnectCountry()
        {
            var country = _configuration["Stripe:Connect:DefaultCountry"]
                ?? _configuration["Stripe:DefaultCountry"]
                ?? "CA";

            country = country.Trim().ToUpperInvariant();
            return country.Length == 2 ? country : "CA";
        }

        private static string? NormalizeCountryIso(string? countryIsoCode)
        {
            var normalized = (countryIsoCode ?? string.Empty).Trim().ToUpperInvariant();
            return normalized.Length == 2 && normalized.All(char.IsLetter)
                ? normalized
                : null;
        }

        private static string? ResolveCountryIsoFromPhoneCode(string? countryCode)
        {
            var normalized = (countryCode ?? string.Empty).Trim();
            if (!normalized.StartsWith('+'))
            {
                normalized = $"+{normalized}";
            }

            return normalized switch
            {
                "+1" => "CA",
                "+237" => "CM",
                "+44" => "GB",
                "+33" => "FR",
                "+32" => "BE",
                "+49" => "DE",
                "+234" => "NG",
                "+225" => "CI",
                "+233" => "GH",
                "+27" => "ZA",
                "+254" => "KE",
                "+971" => "AE",
                _ => null
            };
        }

        private static StripeConnectAccountStatus ReadAccountStatus(JsonElement element)
        {
            return new StripeConnectAccountStatus
            {
                AccountId = ReadDirectString(element, "id") ?? string.Empty,
                DetailsSubmitted = ReadDirectBool(element, "details_submitted"),
                ChargesEnabled = ReadDirectBool(element, "charges_enabled"),
                PayoutsEnabled = ReadDirectBool(element, "payouts_enabled"),
                DisabledReason = FindString(element, "disabled_reason") ?? string.Empty,
                RequirementsSummary = BuildRequirementsSummary(element)
            };
        }

        private static string BuildRequirementsSummary(JsonElement element)
        {
            if (!TryGetDirectElement(element, "requirements", out var requirements) ||
                requirements.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var currentlyDue = ReadStringArray(requirements, "currently_due");
            var eventuallyDue = ReadStringArray(requirements, "eventually_due");
            var pastDue = ReadStringArray(requirements, "past_due");
            var pendingVerification = ReadStringArray(requirements, "pending_verification");

            var parts = new List<string>();
            if (currentlyDue.Count > 0)
            {
                parts.Add($"Missing now: {string.Join(", ", currentlyDue)}");
            }

            if (pastDue.Count > 0)
            {
                parts.Add($"Past due: {string.Join(", ", pastDue)}");
            }

            if (pendingVerification.Count > 0)
            {
                parts.Add($"Pending verification: {string.Join(", ", pendingVerification)}");
            }

            if (parts.Count == 0 && eventuallyDue.Count > 0)
            {
                parts.Add($"May be required later: {string.Join(", ", eventuallyDue)}");
            }

            return string.Join(" | ", parts);
        }

        private static List<string> ReadStringArray(JsonElement element, string propertyName)
        {
            if (!TryGetDirectElement(element, propertyName, out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return new List<string>();
            }

            return array
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
        }

        private static string ExtractStripeMessage(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "Stripe did not provide an error message.";
            }

            try
            {
                using var document = JsonDocument.Parse(raw);
                return FindString(document.RootElement, "message") ?? "Stripe request failed.";
            }
            catch (JsonException)
            {
                return raw;
            }
        }

        private static string? ReadDirectString(JsonElement element, string propertyName)
        {
            if (!TryGetDirectElement(element, propertyName, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        private static bool ReadDirectBool(JsonElement element, string propertyName)
        {
            if (!TryGetDirectElement(element, propertyName, out var value))
            {
                return false;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
                _ => false
            };
        }

        private static bool TryGetDirectElement(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
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
