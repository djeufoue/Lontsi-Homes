using Common.CommunicationModels;
using Common.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentHub.Portal.Services;
using System.Text.Json;

namespace RentHub.Portal.Controllers
{
    [Authorize(Roles = "Tenant")]
    public class TenantController : Controller
    {
        private readonly RentHubApiClient _api;
        private readonly ILogger<TenantController> _logger;

        public TenantController(RentHubApiClient api, ILogger<TenantController> logger)
        {
            _api = api;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            try
            {
                var dashboard = await _api.GetAsync<TenantDashboardDto>("tenancies/tenant-dashboard");
                return View(dashboard);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tenant dashboard failed to load.");
                TempData["Error"] = "Unable to load your tenant dashboard right now. Please try again.";
                return RedirectToAction("Index", "Home");
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> PayRent(int tenancyId, int numberOfPeriods)
        {
            if (tenancyId <= 0 || numberOfPeriods <= 0)
            {
                TempData["Error"] = "Please choose the rent periods to pay.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                await _api.PostAsync<PayRentPeriodsRequest, JsonElement>("payments/rent-periods", new PayRentPeriodsRequest
                {
                    TenancyId = tenancyId,
                    NumberOfPeriods = numberOfPeriods,
                    Method = PaymentMethodEnum.Card
                });

                TempData["Success"] = numberOfPeriods == 1
                    ? "Rent payment was processed for the oldest unpaid period."
                    : $"Rent payment was processed for {numberOfPeriods} consecutive periods.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tenant rent payment failed for tenancy {TenancyId}.", tenancyId);
                TempData["Error"] = SafeUserMessage(ParseApiMessage(ex.Message), "Unable to process the rent payment right now. Please try again.");
            }

            return RedirectToAction(nameof(Index));
        }

        private static string? ParseApiMessage(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.String)
                {
                    return root.GetString();
                }

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    {
                        return message.GetString();
                    }

                    if (root.TryGetProperty("Message", out var pascalMessage) && pascalMessage.ValueKind == JsonValueKind.String)
                    {
                        return pascalMessage.GetString();
                    }
                }
            }
            catch
            {
                return raw;
            }

            return raw;
        }

        private static string SafeUserMessage(string? apiMessage, string fallback)
        {
            if (string.IsNullOrWhiteSpace(apiMessage))
            {
                return fallback;
            }

            var normalized = apiMessage.Trim();
            var technicalFragments = new[] { "exception", "stack trace", "sql", "microsoft.entityframeworkcore", " at " };
            return technicalFragments.Any(fragment => normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                ? fallback
                : normalized;
        }
    }
}
