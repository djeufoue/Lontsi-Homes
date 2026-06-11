using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RentHub.API.Models.Entities;

namespace RentHub.API.Services.Payments
{
    public interface IStripeCheckoutService
    {
        Task<StripeCheckoutResult> CreateSubscriptionCheckoutAsync(
            ApplicationUser user,
            SubscriptionPlan plan,
            UserSubscription subscription,
            long amountInMinorUnits,
            string currency);

        Task<StripeCheckoutResult> CreateRentCheckoutAsync(
            ApplicationUser tenant,
            ApplicationUser landlord,
            Payment payment,
            IReadOnlyCollection<RentPeriod> periods,
            string connectedAccountId,
            long amountInMinorUnits,
            string currency);

        Task<StripeCheckoutStatus?> RetrieveSessionAsync(string sessionId);

        Task<StripeAutomaticPaymentResult> CreateAutomaticSubscriptionPaymentAsync(
            ApplicationUser user,
            SubscriptionPlan plan,
            UserSubscription subscription,
            long amountInMinorUnits,
            string currency,
            string customerId,
            string paymentMethodId);

        bool VerifyWebhookSignature(string rawPayload, string? signatureHeader);
    }

    public sealed class StripeCheckoutResult
    {
        public string SessionId { get; init; } = string.Empty;
        public string ClientSecret { get; init; } = string.Empty;
        public string ReturnUrl { get; init; } = string.Empty;
        public string PaymentStatus { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string CustomerId { get; init; } = string.Empty;
        public string PaymentMethodId { get; init; } = string.Empty;
        public string ProviderReceiptUrl { get; init; } = string.Empty;
    }

    public sealed class StripeCheckoutStatus
    {
        public string SessionId { get; init; } = string.Empty;
        public string ClientSecret { get; init; } = string.Empty;
        public string PaymentReference { get; init; } = string.Empty;
        public string PaymentIntentId { get; init; } = string.Empty;
        public string PaymentStatus { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string CustomerId { get; init; } = string.Empty;
        public string PaymentMethodId { get; init; } = string.Empty;
        public string ProviderReceiptUrl { get; init; } = string.Empty;
    }

    public sealed class StripeAutomaticPaymentResult
    {
        public string PaymentIntentId { get; init; } = string.Empty;
        public string CustomerId { get; init; } = string.Empty;
        public string PaymentMethodId { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string ErrorMessage { get; init; } = string.Empty;
    }

    public class StripeCheckoutService : IStripeCheckoutService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<StripeCheckoutService> _logger;

        public StripeCheckoutService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<StripeCheckoutService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<StripeCheckoutResult> CreateSubscriptionCheckoutAsync(
            ApplicationUser user,
            SubscriptionPlan plan,
            UserSubscription subscription,
            long amountInMinorUnits,
            string currency)
        {
            var portalBaseUrl = _configuration["Portal:BaseUrl"]?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(portalBaseUrl))
            {
                throw new InvalidOperationException("Portal base URL is missing.");
            }

            var returnUrl = $"{portalBaseUrl}/Profile/SubscriptionCallback?reference={Uri.EscapeDataString(subscription.PaymentReference)}&session_id={{CHECKOUT_SESSION_ID}}";
            var normalizedCurrency = NormalizeCurrency(currency);

            var form = new Dictionary<string, string>
            {
                ["mode"] = "payment",
                ["ui_mode"] = "embedded_page",
                ["client_reference_id"] = subscription.PaymentReference,
                ["return_url"] = returnUrl,
                ["customer_email"] = user.Email ?? string.Empty,
                ["payment_method_types[0]"] = "card",
                ["line_items[0][quantity]"] = "1",
                ["line_items[0][price_data][currency]"] = normalizedCurrency,
                ["line_items[0][price_data][unit_amount]"] = amountInMinorUnits.ToString(CultureInfo.InvariantCulture),
                ["line_items[0][price_data][product_data][name]"] = $"Lontsi Homes {plan.Name} subscription",
                ["metadata[paymentReference]"] = subscription.PaymentReference,
                ["metadata[subscriptionId]"] = subscription.Id.ToString(CultureInfo.InvariantCulture),
                ["metadata[planId]"] = plan.Id.ToString(CultureInfo.InvariantCulture),
                ["metadata[userId]"] = user.Id,
                ["payment_intent_data[metadata][paymentReference]"] = subscription.PaymentReference,
                ["payment_intent_data[metadata][subscriptionId]"] = subscription.Id.ToString(CultureInfo.InvariantCulture),
                ["payment_intent_data[metadata][planId]"] = plan.Id.ToString(CultureInfo.InvariantCulture),
                ["payment_intent_data[metadata][userId]"] = user.Id
            };

            if (subscription.AllowAutomaticCardPayments)
            {
                form["customer_creation"] = "always";
                form["payment_intent_data[setup_future_usage]"] = "off_session";
            }

            using var response = await SendStripeFormAsync(HttpMethod.Post, "checkout/sessions", form);
            var raw = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Stripe checkout session creation failed with status {StatusCode}: {Payload}", response.StatusCode, raw);
                throw new InvalidOperationException("Unable to initialize the card payment right now.");
            }

            using var document = JsonDocument.Parse(raw);
            var sessionId = FindString(document.RootElement, "id");
            var clientSecret = FindString(document.RootElement, "client_secret");
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                throw new InvalidOperationException("Stripe did not return a checkout session client secret.");
            }

            return new StripeCheckoutResult
            {
                SessionId = sessionId,
                ClientSecret = clientSecret,
                ReturnUrl = returnUrl.Replace("{CHECKOUT_SESSION_ID}", sessionId, StringComparison.Ordinal),
                PaymentStatus = FindString(document.RootElement, "payment_status") ?? "unpaid",
                Status = FindString(document.RootElement, "status") ?? "open",
                CustomerId = FindString(document.RootElement, "customer") ?? string.Empty,
                PaymentMethodId = FindString(document.RootElement, "payment_method") ?? string.Empty
            };
        }

        public async Task<StripeCheckoutResult> CreateRentCheckoutAsync(
            ApplicationUser tenant,
            ApplicationUser landlord,
            Payment payment,
            IReadOnlyCollection<RentPeriod> periods,
            string connectedAccountId,
            long amountInMinorUnits,
            string currency)
        {
            if (string.IsNullOrWhiteSpace(connectedAccountId))
            {
                throw new InvalidOperationException("The landlord Stripe payout account is missing.");
            }

            var portalBaseUrl = _configuration["Portal:BaseUrl"]?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(portalBaseUrl))
            {
                throw new InvalidOperationException("Portal base URL is missing.");
            }

            var tenancy = payment.Tenancy;
            var propertyName = tenancy?.Apartment?.Property?.Name ?? "Property";
            var apartmentName = tenancy?.Apartment?.Name ?? "Apartment";
            var periodLabel = BuildPeriodLabel(periods);
            var returnUrl = $"{portalBaseUrl}/Tenant/RentPaymentCallback?reference={Uri.EscapeDataString(payment.RequestKey)}&session_id={{CHECKOUT_SESSION_ID}}";
            var normalizedCurrency = NormalizeCurrency(currency);

            var form = new Dictionary<string, string>
            {
                ["mode"] = "payment",
                ["ui_mode"] = "embedded_page",
                ["client_reference_id"] = payment.RequestKey,
                ["return_url"] = returnUrl,
                ["customer_email"] = tenant.Email ?? string.Empty,
                ["payment_method_types[0]"] = "card",
                ["line_items[0][quantity]"] = "1",
                ["line_items[0][price_data][currency]"] = normalizedCurrency,
                ["line_items[0][price_data][unit_amount]"] = amountInMinorUnits.ToString(CultureInfo.InvariantCulture),
                ["line_items[0][price_data][product_data][name]"] = $"Rent payment - {propertyName} / {apartmentName}",
                ["line_items[0][price_data][product_data][description]"] = periodLabel,
                ["metadata[kind]"] = "rent",
                ["metadata[paymentId]"] = payment.Id.ToString(CultureInfo.InvariantCulture),
                ["metadata[paymentReference]"] = payment.RequestKey,
                ["metadata[tenancyId]"] = payment.TenancyId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["metadata[tenantId]"] = tenant.Id,
                ["metadata[landlordId]"] = landlord.Id,
                ["metadata[connectedAccountId]"] = connectedAccountId,
                ["payment_intent_data[transfer_data][destination]"] = connectedAccountId,
                ["payment_intent_data[metadata][kind]"] = "rent",
                ["payment_intent_data[metadata][paymentId]"] = payment.Id.ToString(CultureInfo.InvariantCulture),
                ["payment_intent_data[metadata][paymentReference]"] = payment.RequestKey,
                ["payment_intent_data[metadata][tenancyId]"] = payment.TenancyId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["payment_intent_data[metadata][tenantId]"] = tenant.Id,
                ["payment_intent_data[metadata][landlordId]"] = landlord.Id,
                ["payment_intent_data[metadata][connectedAccountId]"] = connectedAccountId
            };

            using var response = await SendStripeFormAsync(HttpMethod.Post, "checkout/sessions", form);
            var raw = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Stripe rent checkout session creation failed with status {StatusCode}: {Payload}", response.StatusCode, raw);
                throw new InvalidOperationException(ExtractStripeMessage(raw));
            }

            using var document = JsonDocument.Parse(raw);
            var sessionId = FindString(document.RootElement, "id");
            var clientSecret = FindString(document.RootElement, "client_secret");
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                throw new InvalidOperationException("Stripe did not return a rent checkout session client secret.");
            }

            return new StripeCheckoutResult
            {
                SessionId = sessionId,
                ClientSecret = clientSecret,
                ReturnUrl = returnUrl.Replace("{CHECKOUT_SESSION_ID}", sessionId, StringComparison.Ordinal),
                PaymentStatus = FindString(document.RootElement, "payment_status") ?? "unpaid",
                Status = FindString(document.RootElement, "status") ?? "open",
                CustomerId = FindString(document.RootElement, "customer") ?? string.Empty,
                PaymentMethodId = FindString(document.RootElement, "payment_method") ?? string.Empty
            };
        }

        public async Task<StripeCheckoutStatus?> RetrieveSessionAsync(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return null;
            }

            using var response = await SendStripeFormAsync(
                HttpMethod.Get,
                $"checkout/sessions/{Uri.EscapeDataString(sessionId)}",
                new Dictionary<string, string>
                {
                    ["expand[0]"] = "payment_intent",
                    ["expand[1]"] = "payment_intent.latest_charge"
                });

            var raw = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Stripe checkout session lookup failed for {SessionId}: {Payload}", sessionId, raw);
                return null;
            }

            using var document = JsonDocument.Parse(raw);
            return ReadSession(document.RootElement, sessionId);
        }

        public async Task<StripeAutomaticPaymentResult> CreateAutomaticSubscriptionPaymentAsync(
            ApplicationUser user,
            SubscriptionPlan plan,
            UserSubscription subscription,
            long amountInMinorUnits,
            string currency,
            string customerId,
            string paymentMethodId)
        {
            if (string.IsNullOrWhiteSpace(customerId) || string.IsNullOrWhiteSpace(paymentMethodId))
            {
                return new StripeAutomaticPaymentResult
                {
                    Status = "failed",
                    ErrorMessage = "Saved card information is missing."
                };
            }

            var normalizedCurrency = NormalizeCurrency(currency);
            var form = new Dictionary<string, string>
            {
                ["amount"] = amountInMinorUnits.ToString(CultureInfo.InvariantCulture),
                ["currency"] = normalizedCurrency,
                ["customer"] = customerId,
                ["payment_method"] = paymentMethodId,
                ["off_session"] = "true",
                ["confirm"] = "true",
                ["description"] = $"Lontsi Homes {plan.Name} subscription renewal",
                ["metadata[paymentReference]"] = subscription.PaymentReference,
                ["metadata[subscriptionId]"] = subscription.Id.ToString(CultureInfo.InvariantCulture),
                ["metadata[planId]"] = plan.Id.ToString(CultureInfo.InvariantCulture),
                ["metadata[userId]"] = user.Id,
                ["metadata[automaticRenewal]"] = "true"
            };

            using var response = await SendStripeFormAsync(HttpMethod.Post, "payment_intents", form);
            var raw = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Stripe automatic subscription payment failed for {UserEmail} ({UserId}), SubscriptionId={SubscriptionId}: {Payload}",
                    user.Email ?? string.Empty,
                    user.Id,
                    subscription.Id,
                    raw);

                return new StripeAutomaticPaymentResult
                {
                    Status = "failed",
                    CustomerId = customerId,
                    PaymentMethodId = paymentMethodId,
                    ErrorMessage = ExtractStripeMessage(raw)
                };
            }

            using var document = JsonDocument.Parse(raw);
            return new StripeAutomaticPaymentResult
            {
                PaymentIntentId = ReadDirectString(document.RootElement, "id") ?? string.Empty,
                CustomerId = ReadDirectString(document.RootElement, "customer") ?? customerId,
                PaymentMethodId = ReadDirectString(document.RootElement, "payment_method") ?? paymentMethodId,
                Status = ReadDirectString(document.RootElement, "status") ?? "unknown"
            };
        }

        public bool VerifyWebhookSignature(string rawPayload, string? signatureHeader)
        {
            var secret = _configuration["Stripe:WebhookSecret"]?.Trim();
            if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signatureHeader))
            {
                return false;
            }

            var parts = signatureHeader
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
                .Where(part => part.Length == 2)
                .GroupBy(part => part[0], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(part => part[1]).ToList(), StringComparer.OrdinalIgnoreCase);

            if (!parts.TryGetValue("t", out var timestamps) || timestamps.Count == 0 ||
                !parts.TryGetValue("v1", out var signatures) || signatures.Count == 0)
            {
                return false;
            }

            var signedPayload = $"{timestamps[0]}.{rawPayload}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload))).ToLowerInvariant();

            return signatures.Any(signature => FixedTimeEquals(expected, signature.Trim().ToLowerInvariant()));
        }

        public static StripeCheckoutStatus? ReadSession(JsonElement element, string fallbackSessionId = "")
        {
            var sessionId = ReadDirectString(element, "id") ?? fallbackSessionId;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return null;
            }

            var paymentIntentId = ReadDirectString(element, "payment_intent") ?? string.Empty;
            var customerId = ReadDirectString(element, "customer") ?? string.Empty;
            var paymentMethodId = string.Empty;

            if (TryGetDirectElement(element, "payment_intent", out var paymentIntentElement) &&
                paymentIntentElement.ValueKind == JsonValueKind.Object)
            {
                paymentIntentId = ReadDirectString(paymentIntentElement, "id") ?? paymentIntentId;
                customerId = ReadDirectString(paymentIntentElement, "customer") ?? customerId;
                paymentMethodId = ReadDirectString(paymentIntentElement, "payment_method") ?? paymentMethodId;
            }

            return new StripeCheckoutStatus
            {
                SessionId = sessionId,
                ClientSecret = ReadDirectString(element, "client_secret") ?? string.Empty,
                PaymentReference = ReadDirectString(element, "client_reference_id")
                    ?? FindString(element, "paymentReference")
                    ?? string.Empty,
                PaymentIntentId = paymentIntentId,
                PaymentStatus = ReadDirectString(element, "payment_status") ?? "unpaid",
                Status = ReadDirectString(element, "status") ?? string.Empty,
                CustomerId = customerId,
                PaymentMethodId = paymentMethodId,
                ProviderReceiptUrl = FindString(element, "receipt_url") ?? string.Empty
            };
        }

        private static string BuildPeriodLabel(IReadOnlyCollection<RentPeriod> periods)
        {
            if (periods.Count == 0)
            {
                return "Rent payment";
            }

            var ordered = periods.OrderBy(period => period.PeriodStart).ToList();
            var first = ordered.First();
            var last = ordered.Last();
            return ordered.Count == 1
                ? $"{first.PeriodStart:MMM d, yyyy} - {first.PeriodEnd:MMM d, yyyy}"
                : $"{first.PeriodStart:MMM d, yyyy} - {last.PeriodEnd:MMM d, yyyy}";
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
            var requestPath = path.TrimStart('/');
            if (method == HttpMethod.Get && form?.Count > 0)
            {
                var query = string.Join("&", form.Select(item =>
                    $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
                requestPath = requestPath.Contains('?', StringComparison.Ordinal)
                    ? $"{requestPath}&{query}"
                    : $"{requestPath}?{query}";
            }

            using var request = new HttpRequestMessage(method, $"{baseUrl}/v1/{requestPath}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secretKey);
            if (method != HttpMethod.Get && form != null)
            {
                request.Content = new FormUrlEncodedContent(form);
            }

            var client = _httpClientFactory.CreateClient();
            return await client.SendAsync(request);
        }

        private static string NormalizeCurrency(string? currency)
        {
            var normalized = (currency ?? "usd").Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(normalized) ? "usd" : normalized;
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            var leftBytes = Encoding.UTF8.GetBytes(left);
            var rightBytes = Encoding.UTF8.GetBytes(right);
            return leftBytes.Length == rightBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }

        private static string? ReadDirectString(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

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
            }

            return null;
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

        private static string ExtractStripeMessage(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "Stripe did not provide an error message.";
            }

            try
            {
                using var document = JsonDocument.Parse(raw);
                return FindString(document.RootElement, "message") ?? "Stripe payment failed.";
            }
            catch (JsonException)
            {
                return raw;
            }
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
