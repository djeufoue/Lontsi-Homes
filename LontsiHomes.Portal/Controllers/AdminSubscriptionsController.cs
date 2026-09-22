using Common.CommunicationModels;
using Common.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LontsiHomes.Portal.Services;
using LontsiHomes.Portal.ViewModels.AdminSubscriptions;

namespace LontsiHomes.Portal.Controllers
{
    [Authorize(Roles = "Admin")]
    public class AdminSubscriptionsController : Controller
    {
        private readonly LontsiHomesApiClient _api;

        public AdminSubscriptionsController(LontsiHomesApiClient api)
        {
            _api = api;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? search = null)
        {
            var plans = await _api.GetAsync<List<SubscriptionPlanDto>>("Subscriptions/plans");
            var endpoint = "Subscriptions/admin";
            if (!string.IsNullOrWhiteSpace(search))
            {
                endpoint += $"?search={Uri.EscapeDataString(search.Trim())}";
            }
            var subscriptions = await _api.GetAsync<List<PendingSubscriptionDto>>(endpoint);

            return View(new AdminSubscriptionsIndexVm
            {
                Plans = plans,
                Subscriptions = subscriptions,
                Search = search?.Trim() ?? string.Empty
            });
        }

        [HttpGet]
        public async Task<IActionResult> History(int subscriptionId)
        {
            var model = await _api.GetAsync<AdminSubscriptionHistoryDto>(
                $"Subscriptions/admin/{subscriptionId}/history");
            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> PaymentAccounts()
        {
            var transferAccounts = await _api.GetAsync<List<SystemTransferAccountDto>>("AdminTransferAccounts");
            var plans = await _api.GetAsync<List<SubscriptionPlanDto>>("Subscriptions/plans");

            return View(new AdminSubscriptionsIndexVm
            {
                Plans = plans,
                TransferAccounts = transferAccounts
            });
        }

        [HttpGet]
        public async Task<IActionResult> PaymentActivity(
            int page = 1,
            int pageSize = 25,
            string? search = null,
            string? status = null)
        {
            var query = new List<string>
            {
                $"page={Math.Max(1, page)}",
                $"pageSize={Math.Clamp(pageSize, 10, 100)}"
            };

            if (!string.IsNullOrWhiteSpace(search))
            {
                query.Add($"search={Uri.EscapeDataString(search.Trim())}");
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                query.Add($"status={Uri.EscapeDataString(status.Trim())}");
            }

            var response = await _api.GetAsync<SubscriptionPaymentActivityResponseDto>(
                $"Subscriptions/payment-activity?{string.Join("&", query)}");

            return View(response);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Approve(int subscriptionId, string? search = null)
        {
            try
            {
                await _api.PostAsync($"Subscriptions/approve/{subscriptionId}", new { });
                TempData["Success"] = "Subscription approved successfully.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = ExtractMessage(ex.Message);
            }

            return RedirectToAction(nameof(Index), new { search });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Reject(int subscriptionId, string? search = null)
        {
            try
            {
                await _api.PostAsync($"Subscriptions/reject/{subscriptionId}", new { });
                TempData["Success"] = "Subscription request rejected.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = ExtractMessage(ex.Message);
            }

            return RedirectToAction(nameof(Index), new { search });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateAutomaticPayments(bool enabled)
        {
            try
            {
                await _api.PutAsync("PaymentSettings/platform", new UpdateAutomaticPaymentAvailabilityRequest
                {
                    Enabled = enabled
                });
                TempData["Success"] = enabled
                    ? "Automatic payments are enabled for the platform. Properties can now be enabled individually."
                    : "Automatic payments are disabled across the platform and automatic renewals were turned off.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = ExtractMessage(ex.Message);
            }

            return RedirectToAction(nameof(PaymentActivity));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdatePlan(UpdateSubscriptionPlanAdminVm vm)
        {
            if (!ModelState.IsValid)
            {
                TempData["Error"] = "Please provide valid plan values before saving.";
                return RedirectToAction(nameof(PaymentAccounts));
            }

            if (!vm.UnlimitedProperties && (!vm.MaxProperties.HasValue || vm.MaxProperties.Value < 0))
            {
                TempData["Error"] = "Max properties must be 0 or greater, or select unlimited.";
                return RedirectToAction(nameof(PaymentAccounts));
            }

            if (!vm.UnlimitedApartmentsPerProperty && (!vm.MaxApartmentsPerProperty.HasValue || vm.MaxApartmentsPerProperty.Value < 0))
            {
                TempData["Error"] = "Max apartments per property must be 0 or greater, or select unlimited.";
                return RedirectToAction(nameof(PaymentAccounts));
            }

            if (!vm.UnlimitedTotalApartments && (!vm.MaxTotalApartments.HasValue || vm.MaxTotalApartments.Value < 0))
            {
                TempData["Error"] = "Max total apartments must be 0 or greater, or select unlimited.";
                return RedirectToAction(nameof(PaymentAccounts));
            }

            try
            {
                var req = new UpdateSubscriptionPlanRequest
                {
                    Name = vm.Name,
                    Description = vm.Description,
                    Price = vm.Price,
                    AnnualPrice = vm.AnnualPrice,
                    DurationInDays = vm.DurationInDays,
                    MaxProperties = vm.UnlimitedProperties ? -1 : vm.MaxProperties,
                    MaxApartmentsPerProperty = vm.UnlimitedApartmentsPerProperty ? -1 : vm.MaxApartmentsPerProperty,
                    MaxTotalApartments = vm.UnlimitedTotalApartments ? -1 : vm.MaxTotalApartments,
                    AudienceLabel = vm.AudienceLabel,
                    FeatureHighlights = vm.FeatureHighlights,
                    IsRecommended = vm.IsRecommended,
                    IsContactSales = vm.IsContactSales,
                    DisplayOrder = vm.DisplayOrder
                };

                await _api.PutAsync($"Subscriptions/plans/{vm.PlanId}", req);
                TempData["Success"] = $"Plan '{vm.Name}' updated successfully.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = ExtractMessage(ex.Message);
            }

            return RedirectToAction(nameof(PaymentAccounts));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateTransferAccount(UpdateTransferAccountAdminVm vm)
        {
            if (!ModelState.IsValid)
            {
                TempData["Error"] = "Please provide a valid receiving account name and check any optional phone details.";
                return RedirectToAction(nameof(PaymentAccounts));
            }

            if (vm.Channel != PayoutChannelEnum.MtnMoney && vm.Channel != PayoutChannelEnum.OrangeMoney)
            {
                TempData["Error"] = "Only MTN Money and Orange Money are supported in the admin transfer section.";
                return RedirectToAction(nameof(PaymentAccounts));
            }

            try
            {
                var request = new UpsertSystemTransferAccountRequest
                {
                    Channel = vm.Channel,
                    AccountName = vm.AccountName,
                    PhoneNumber = vm.PhoneNumber,
                    CountryCode = vm.CountryCode,
                    Notes = vm.Notes
                };

                await _api.PutAsync($"AdminTransferAccounts/{vm.Channel}", request);
                TempData["Success"] = $"{(vm.Channel == PayoutChannelEnum.MtnMoney ? "MTN Money" : "Orange Money")} receiving account saved successfully.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = ExtractMessage(ex.Message);
            }

            return RedirectToAction(nameof(PaymentAccounts));
        }

        private static string ExtractMessage(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Request failed.";

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    root.TryGetProperty("Message", out var message) &&
                    message.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return message.GetString() ?? "Request failed.";
                }

                if (root.ValueKind == System.Text.Json.JsonValueKind.String)
                    return root.GetString() ?? "Request failed.";
            }
            catch
            {
            }

            return raw;
        }
    }
}
