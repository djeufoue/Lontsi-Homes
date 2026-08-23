using Common.CommunicationModels;
using Common.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.Tenant;
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
        public IActionResult Index() => RedirectToAction("Index", "Tenancies");

        [HttpGet]
        public IActionResult PaymentDetails(int tenancyId)
        {
            return tenancyId <= 0
                ? RedirectToAction("Index", "Tenancies")
                : RedirectToAction(nameof(Tenancy), new { tenancyId });
        }

        [HttpGet]
        public async Task<IActionResult> Tenancy(
            int tenancyId,
            int periodPage = 1,
            int periodPageSize = 10,
            int historyPage = 1,
            int historyPageSize = 10)
        {
            if (tenancyId <= 0) return RedirectToAction("Index", "Tenancies");

            try
            {
                var dashboard = await _api.GetAsync<TenantDashboardDto>("tenancies/tenant-dashboard");
                var tenancy = dashboard.Tenancies.FirstOrDefault(item => item.Tenancy.Id == tenancyId);
                if (tenancy == null)
                {
                    TempData["Error"] = "The requested tenancy could not be found.";
                    return RedirectToAction("Index", "Tenancies");
                }

                periodPageSize = NormalizePageSize(periodPageSize);
                historyPageSize = NormalizePageSize(historyPageSize);

                var rentPeriods = tenancy.RentPeriods
                    .OrderByDescending(period => period.PeriodStart)
                    .ToList();
                var paymentHistory = tenancy.PaymentHistory
                    .OrderByDescending(payment => payment.PaymentDate)
                    .ToList();

                var totalPeriodPages = PageCount(rentPeriods.Count, periodPageSize);
                var totalHistoryPages = PageCount(paymentHistory.Count, historyPageSize);
                periodPage = Math.Clamp(periodPage, 1, totalPeriodPages);
                historyPage = Math.Clamp(historyPage, 1, totalHistoryPages);

                return View(new TenantTenancyVm
                {
                    Item = tenancy,
                    RentPeriods = rentPeriods
                        .Skip((periodPage - 1) * periodPageSize)
                        .Take(periodPageSize)
                        .ToList(),
                    PeriodPage = periodPage,
                    PeriodPageSize = periodPageSize,
                    TotalRentPeriods = rentPeriods.Count,
                    TotalPeriodPages = totalPeriodPages,
                    PaymentHistory = paymentHistory
                        .Skip((historyPage - 1) * historyPageSize)
                        .Take(historyPageSize)
                        .ToList(),
                    HistoryPage = historyPage,
                    HistoryPageSize = historyPageSize,
                    TotalPayments = paymentHistory.Count,
                    TotalHistoryPages = totalHistoryPages
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load tenant tenancy workspace for tenancy {TenancyId}.", tenancyId);
                TempData["Error"] = "Unable to load your tenancy right now.";
                return RedirectToAction(nameof(Index));
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> PayRent(int tenancyId, int numberOfPeriods, PaymentMethodEnum paymentMethod = PaymentMethodEnum.Card)
        {
            if (tenancyId <= 0 || numberOfPeriods <= 0)
            {
                TempData["Error"] = "Please choose the rent periods to pay.";
                return RedirectToAction(nameof(Tenancy), new { tenancyId });
            }

            if (paymentMethod == PaymentMethodEnum.Cash)
            {
                TempData["Error"] = "Cash payments must be recorded by the landlord.";
                return RedirectToAction(nameof(Tenancy), new { tenancyId });
            }

            try
            {
                var request = new PayRentPeriodsRequest
                {
                    TenancyId = tenancyId,
                    NumberOfPeriods = numberOfPeriods,
                    Method = paymentMethod
                };

                if (paymentMethod == PaymentMethodEnum.Card)
                {
                    var session = await _api.PostAsync<PayRentPeriodsRequest, RentCheckoutSessionDto>(
                        "payments/rent-periods",
                        request);

                    if (string.IsNullOrWhiteSpace(session.PaymentReference))
                    {
                        TempData["Error"] = "Card checkout could not be started. Please try again.";
                        return RedirectToAction(nameof(Tenancy), new { tenancyId });
                    }

                    return RedirectToAction(nameof(RentCardCheckout), new { reference = session.PaymentReference });
                }

                await _api.PostAsync<PayRentPeriodsRequest, JsonElement>("payments/rent-periods", request);

                TempData["Success"] = numberOfPeriods == 1
                    ? "Rent payment was processed for the oldest unpaid period."
                    : $"Rent payment was processed for {numberOfPeriods} consecutive periods.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tenant rent payment failed for tenancy {TenancyId}.", tenancyId);
                TempData["Error"] = SafeUserMessage(ParseApiMessage(ex.Message), "Unable to process the rent payment right now. Please try again.");
            }

            return RedirectToAction(nameof(Tenancy), new { tenancyId });
        }

        [HttpGet]
        public async Task<IActionResult> RentCardCheckout(string? reference = null)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                TempData["Error"] = "Card checkout reference is missing. Please choose the rent periods again.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                var session = await _api.GetAsync<RentCheckoutSessionDto>(
                    $"payments/rent-periods/checkout-session/{Uri.EscapeDataString(reference)}");

                if (string.IsNullOrWhiteSpace(session.PublishableKey) ||
                    string.IsNullOrWhiteSpace(session.ClientSecret))
                {
                    TempData["Error"] = "Card checkout is not configured yet. Please try again later.";
                    return RedirectToAction(nameof(Index));
                }

                return View(new RentCardCheckoutVm
                {
                    PaymentId = session.PaymentId,
                    TenancyId = session.TenancyId,
                    RentAmount = session.RentAmount,
                    RentCurrency = session.RentCurrency,
                    ChargeAmount = session.ChargeAmount,
                    ChargeCurrency = session.ChargeCurrency,
                    PaymentReference = session.PaymentReference,
                    ProviderReference = session.ProviderReference,
                    PublishableKey = session.PublishableKey,
                    ClientSecret = session.ClientSecret,
                    ReturnUrl = session.ReturnUrl,
                    PropertyName = session.PropertyName,
                    ApartmentName = session.ApartmentName,
                    PeriodLabel = session.PeriodLabel
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load rent card checkout for reference {Reference}.", reference);
                TempData["Error"] = SafeUserMessage(
                    ParseApiMessage(ex.Message),
                    "Unable to load the secure card form right now. Please try again.");
                return RedirectToAction(nameof(Index));
            }
        }

        [HttpGet]
        public async Task<IActionResult> RentPaymentCallback(string? reference = null, string? session_id = null)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                TempData["Error"] = "Rent payment reference is missing.";
                return RedirectToAction(nameof(Index));
            }

            int? tenancyId = null;
            try
            {
                var status = await _api.GetAsync<RentCheckoutStatusDto>(
                    $"payments/rent-periods/checkout-status/{Uri.EscapeDataString(reference)}");
                tenancyId = status.TenancyId > 0 ? status.TenancyId : null;

                TempData[status.PaymentCompleted ? "Success" : "Error"] = string.IsNullOrWhiteSpace(status.Message)
                    ? (status.PaymentCompleted ? "Rent payment completed successfully." : "The card payment was not completed. Please try again.")
                    : status.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rent payment callback lookup failed for reference {Reference}.", reference);
                TempData["Error"] = SafeUserMessage(
                    ParseApiMessage(ex.Message),
                    "Unable to verify the rent payment right now. Please refresh your rent dashboard in a moment.");
            }

            return tenancyId.HasValue
                ? RedirectToAction(nameof(Tenancy), new { tenancyId = tenancyId.Value })
                : RedirectToAction(nameof(Index));
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

        private static int NormalizePageSize(int pageSize)
            => pageSize is 5 or 10 or 20 or 50 ? pageSize : 10;

        private static int PageCount(int totalCount, int pageSize)
            => Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));

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
