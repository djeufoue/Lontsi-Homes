using Common.CommunicationModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.AdminUsers;
using System.Text.Json;

namespace RentHub.Portal.Controllers
{
    [Authorize(Roles = "Admin")]
    public class AdminUsersController : Controller
    {
        private readonly RentHubApiClient _api;

        public AdminUsersController(RentHubApiClient api)
        {
            _api = api;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? search = null)
        {
            var endpoint = string.IsNullOrWhiteSpace(search)
                ? "AdminUsers/verification-status"
                : $"AdminUsers/verification-status?search={Uri.EscapeDataString(search.Trim())}";

            var usersTask = _api.GetAsync<List<AdminUserVerificationStatusDto>>(endpoint);
            var permissionsTask = _api.GetAsync<AdminUserManagementPermissionsDto>("AdminUsers/management-permissions");

            await Task.WhenAll(usersTask, permissionsTask);

            return View(new AdminUsersIndexVm
            {
                Search = search,
                CanDeleteUsers = permissionsTask.Result.CanDeleteUsers,
                Users = usersTask.Result
            });
        }

        [HttpGet]
        public async Task<IActionResult> Overview(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                TempData["Error"] = "User id is required.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                var overview = await _api.GetAsync<AdminUserOverviewDto>($"AdminUsers/{Uri.EscapeDataString(userId)}/overview");
                return View(new AdminUserOverviewVm
                {
                    Overview = overview
                });
            }
            catch (Exception ex)
            {
                TempData["Error"] = ExtractMessage(ex.Message);
                return RedirectToAction(nameof(Index));
            }
        }

        [HttpGet]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> OtpStatus(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return BadRequest(new { Message = "User id is required." });
            }

            var otpCodes = await _api.GetAsync<List<AdminUserOtpDto>>($"AdminUsers/{Uri.EscapeDataString(userId)}/otp-status");
            return Json(otpCodes);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RestartValidation(string userId, string? search = null)
        {
            try
            {
                await _api.PostAsync($"AdminUsers/{userId}/restart-validation", new { });
                TempData["Success"] = "Validation restarted for the selected user.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = ExtractMessage(ex.Message);
            }

            return RedirectToAction(nameof(Index), new { search });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteUser(string userId, string? search = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(userId))
                {
                    TempData["Error"] = "User id is required.";
                    return RedirectToAction(nameof(Index), new { search });
                }

                await _api.DeleteAsync($"AdminUsers/{Uri.EscapeDataString(userId)}");
                TempData["Success"] = "User and related records were deleted.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = ExtractMessage(ex.Message);
            }

            return RedirectToAction(nameof(Index), new { search });
        }

        private static string ExtractMessage(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Request failed.";

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("Message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? "Request failed.";
                }

                if (root.ValueKind == JsonValueKind.String)
                    return root.GetString() ?? "Request failed.";
            }
            catch
            {
            }

            return raw;
        }
    }
}
