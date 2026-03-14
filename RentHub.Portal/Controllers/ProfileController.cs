using Common.CommunicationModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.Profile;
using System.Text.Json;

namespace RentHub.Portal.Controllers
{
    [Authorize]
    public class ProfileController : Controller
    {
        private readonly RentHubApiClient _api;
        private readonly ILogger<ProfileController> _logger;

        public ProfileController(RentHubApiClient api, ILogger<ProfileController> logger)
        {
            _api = api;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            try
            {
                var overview = await _api.GetAsync<ProfileOverviewDto>("Account/profile-overview");
                return View(new ProfileIndexVm
                {
                    Overview = overview,
                    NowUtc = DateTimeOffset.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load profile overview in Portal");
                TempData["Error"] = "Unable to load your profile right now. Please try again.";
                return RedirectToAction("Index", "Home");
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Upgrade(int planId)
        {
            if (planId <= 0)
            {
                TempData["Error"] = "Please choose a valid subscription plan.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                var response = await _api.PostAsync<object, JsonElement>($"Subscriptions/subscribe/{planId}", new { });
                var message = ReadMessage(response);

                TempData["Success"] = SafeUserMessage(
                    message,
                    "Subscription updated successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Subscription upgrade failed for plan {PlanId}", planId);

                var apiMessage = ParseApiMessage(ex.Message);
                TempData["Error"] = SafeUserMessage(
                    apiMessage,
                    "Unable to update your subscription right now. Please try again.");
            }

            return RedirectToAction(nameof(Index));
        }

        private static string? ReadMessage(JsonElement element)
        {
            if (TryGetPropertyIgnoreCase(element, "message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String)
            {
                return messageElement.GetString();
            }

            return null;
        }

        private static string? ParseApiMessage(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.String)
                    return root.GetString();

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetPropertyIgnoreCase(root, "message", out var messageElement) &&
                        messageElement.ValueKind == JsonValueKind.String)
                    {
                        return messageElement.GetString();
                    }
                }
            }
            catch
            {
                // Fallback to raw message below.
            }

            return raw;
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
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

        private static string SafeUserMessage(string? apiMessage, string fallback)
        {
            if (string.IsNullOrWhiteSpace(apiMessage))
                return fallback;

            return LooksTechnicalMessage(apiMessage) ? fallback : apiMessage;
        }

        private static bool LooksTechnicalMessage(string message)
        {
            var normalized = message.Trim();
            if (normalized.Length == 0)
                return true;

            var technicalFragments = new[]
            {
                "exception",
                "stack trace",
                "inner exception",
                "dbupdateexception",
                "sqlexception",
                "invalid column name",
                "entityframework",
                " at "
            };

            return technicalFragments.Any(fragment =>
                normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }
    }
}
