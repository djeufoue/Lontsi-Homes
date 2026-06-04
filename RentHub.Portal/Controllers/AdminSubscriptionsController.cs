using Common.CommunicationModels;
using Common.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.AdminSubscriptions;

namespace RentHub.Portal.Controllers
{
    [Authorize(Roles = "Admin")]
    public class AdminSubscriptionsController : Controller
    {
        private readonly RentHubApiClient _api;

        public AdminSubscriptionsController(RentHubApiClient api)
        {
            _api = api;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var plans = await _api.GetAsync<List<SubscriptionPlanDto>>("Subscriptions/plans");
            var pending = await _api.GetAsync<List<PendingSubscriptionDto>>("Subscriptions/pending");

            return View(new AdminSubscriptionsIndexVm
            {
                Plans = plans,
                PendingSubscriptions = pending
            });
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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Approve(int subscriptionId)
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

            return RedirectToAction(nameof(Index));
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

            try
            {
                var req = new UpdateSubscriptionPlanRequest
                {
                    Name = vm.Name,
                    Description = vm.Description,
                    Price = vm.Price,
                    DurationInDays = vm.DurationInDays,
                    MaxProperties = vm.UnlimitedProperties ? -1 : vm.MaxProperties,
                    MaxApartmentsPerProperty = vm.UnlimitedApartmentsPerProperty ? -1 : vm.MaxApartmentsPerProperty
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
                TempData["Error"] = "Please provide a valid receiving account name, number, and country code.";
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
