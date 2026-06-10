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
        bool IsConnectPlatformEnabled();
        string GetPlatformNotReadyMessage();
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

    public sealed class StripeConnectPlatformNotReadyException : InvalidOperationException
    {
        public StripeConnectPlatformNotReadyException(string message)
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
            if (!IsConnectPlatformEnabled())
            {
                throw new StripeConnectPlatformNotReadyException(GetPlatformNotReadyMessage());
            }

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
                throw BuildStripeRequestException(raw);
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
                throw BuildStripeRequestException(raw);
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

        public bool IsConnectPlatformEnabled()
        {
            return _configuration.GetValue<bool?>("Stripe:Connect:Enabled").GetValueOrDefault(false);
        }

        public string GetPlatformNotReadyMessage()
        {
            return "Stripe Connect setup is temporarily unavailable while the platform owner completes Stripe Connect activation in the Stripe Dashboard. Please continue testing the rest of the platform for now.";
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
                DisabledReason = FormatDisabledReason(FindString(element, "disabled_reason")),
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

            var pastDueRaw = ReadStringArray(requirements, "past_due");
            var pastDueSet = new HashSet<string>(pastDueRaw, StringComparer.OrdinalIgnoreCase);
            var currentlyDue = FormatRequirementList(ReadStringArray(requirements, "currently_due")
                .Where(value => !pastDueSet.Contains(value)));
            var eventuallyDue = FormatRequirementList(ReadStringArray(requirements, "eventually_due"));
            var pastDue = FormatRequirementList(pastDueRaw);
            var pendingVerification = FormatRequirementList(ReadStringArray(requirements, "pending_verification"));

            var parts = new List<string>();
            if (currentlyDue.Count > 0)
            {
                parts.Add($"Action needed now: {JoinRequirementList(currentlyDue)}.");
            }

            if (pastDue.Count > 0)
            {
                parts.Add($"Overdue: {JoinRequirementList(pastDue)}. Payouts stay waiting until this is completed in Stripe.");
            }

            if (pendingVerification.Count > 0)
            {
                parts.Add($"Pending review: Stripe is checking {JoinRequirementList(pendingVerification)}.");
            }

            if (parts.Count == 0 && eventuallyDue.Count > 0)
            {
                parts.Add($"May be required later: {JoinRequirementList(eventuallyDue)}.");
            }

            return string.Join(" ", parts);
        }

        private static string FormatDisabledReason(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim();
            return normalized switch
            {
                "requirements.past_due" => "Stripe needs overdue verification details before payouts can be enabled.",
                "requirements.pending_verification" => "Stripe is reviewing the submitted payout account details.",
                "requirements.eventually_due" => "Stripe will need more payout account details before payouts are fully enabled.",
                "requirements.fields_needed" => "Stripe needs more payout account details before payouts can be enabled.",
                "listed" => "Stripe paused this payout account for review.",
                "rejected.fraud" => "Stripe rejected this payout account after risk review.",
                "rejected.terms_of_service" => "Stripe rejected this payout account because required terms or compliance steps were not accepted.",
                _ when normalized.StartsWith("requirements.", StringComparison.OrdinalIgnoreCase)
                    => $"Stripe needs more verification details: {HumanizeStripeCode(normalized)}.",
                _ => HumanizeStripeCode(normalized)
            };
        }

        private static List<string> FormatRequirementList(IEnumerable<string> values)
        {
            return values
                .Select(FormatRequirement)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string FormatRequirement(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim();
            return normalized switch
            {
                "external_account" => "add a payout bank account",
                "business_profile.mcc" => "select the business industry",
                "business_profile.product_description" => "describe the product or service",
                "business_profile.url" => "confirm the website",
                "individual.email" => "confirm the email address",
                "individual.first_name" => "confirm the legal first name",
                "individual.last_name" => "confirm the legal last name",
                "individual.phone" => "confirm the phone number",
                "individual.verification.additional_document" => "upload an additional identity document",
                "individual.verification.document" => "upload an identity document",
                "individual.verification.proof_of_liveness" => "complete the Stripe identity/liveness check",
                "tos_acceptance.date" => "accept Stripe terms of service",
                "tos_acceptance.ip" => "accept Stripe terms of service",
                _ when normalized.StartsWith("individual.address.", StringComparison.OrdinalIgnoreCase)
                    => "confirm the home address",
                _ when normalized.StartsWith("individual.dob.", StringComparison.OrdinalIgnoreCase)
                    => "confirm the date of birth",
                _ when normalized.StartsWith("company.", StringComparison.OrdinalIgnoreCase)
                    => $"complete company information ({HumanizeStripeCode(normalized)})",
                _ when normalized.StartsWith("representative.", StringComparison.OrdinalIgnoreCase)
                    => $"complete representative information ({HumanizeStripeCode(normalized)})",
                _ when normalized.StartsWith("owners.", StringComparison.OrdinalIgnoreCase)
                    => $"complete owner information ({HumanizeStripeCode(normalized)})",
                _ => HumanizeStripeCode(normalized)
            };
        }

        private static string JoinRequirementList(IReadOnlyList<string> values)
        {
            if (values.Count == 0)
            {
                return string.Empty;
            }

            if (values.Count == 1)
            {
                return values[0];
            }

            if (values.Count == 2)
            {
                return $"{values[0]} and {values[1]}";
            }

            return $"{string.Join(", ", values.Take(values.Count - 1))}, and {values[values.Count - 1]}";
        }

        private static string HumanizeStripeCode(string value)
        {
            return value
                .Replace("requirements.", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("business_profile.", "business profile ", StringComparison.OrdinalIgnoreCase)
                .Replace("individual.", "personal ", StringComparison.OrdinalIgnoreCase)
                .Replace('_', ' ')
                .Replace('.', ' ')
                .Trim();
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

        private InvalidOperationException BuildStripeRequestException(string raw)
        {
            var message = ExtractStripeMessage(raw);
            return IsConnectSignupRequiredMessage(message)
                ? new StripeConnectPlatformNotReadyException(GetPlatformNotReadyMessage())
                : new InvalidOperationException(message);
        }

        private static bool IsConnectSignupRequiredMessage(string message)
        {
            return message.Contains("signed up for Connect", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("dashboard.stripe.com/connect", StringComparison.OrdinalIgnoreCase);
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
