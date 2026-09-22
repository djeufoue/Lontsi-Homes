using Common.CommunicationModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LontsiHomes.Portal.Services;
using LontsiHomes.Portal.ViewModels.AdminUsers;
using System.Text.Json;

namespace LontsiHomes.Portal.Controllers
{
    [Authorize]
    public class AdminUsersController : Controller
    {
        private readonly LontsiHomesApiClient _api;

        public AdminUsersController(LontsiHomesApiClient api)
        {
            _api = api;
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
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
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Landlords(string? search = null)
        {
            var endpoint = string.IsNullOrWhiteSpace(search)
                ? "AdminUsers/landlord-verification-status"
                : $"AdminUsers/landlord-verification-status?search={Uri.EscapeDataString(search.Trim())}";

            var landlordsTask = _api.GetAsync<List<AdminUserVerificationStatusDto>>(endpoint);
            var permissionsTask = _api.GetAsync<AdminUserManagementPermissionsDto>("AdminUsers/management-permissions");

            await Task.WhenAll(landlordsTask, permissionsTask);

            return View(new AdminLandlordsIndexVm
            {
                Search = search,
                CanDeleteUsers = permissionsTask.Result.CanDeleteUsers,
                Landlords = landlordsTask.Result
            });
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateSubscriptionExemption(
            string userId,
            bool subscriptionExempt,
            string? search = null)
        {
            try
            {
                await _api.PutAsync(
                    $"AdminUsers/{Uri.EscapeDataString(userId)}/subscription-exemption",
                    new UpdateLandlordSubscriptionExemptionRequest { Enabled = subscriptionExempt });
            }
            catch (Exception ex)
            {
                TempData["Error"] = ExtractMessage(ex.Message);
            }

            return RedirectToAction(nameof(Landlords), new { search });
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> LandlordApprovals(string? search = null)
        {
            var endpoint = string.IsNullOrWhiteSpace(search)
                ? "AdminUsers/landlord-approvals"
                : $"AdminUsers/landlord-approvals?search={Uri.EscapeDataString(search.Trim())}";

            var landlords = await _api.GetAsync<List<AdminLandlordApprovalDto>>(endpoint);
            return View(new AdminLandlordApprovalsVm
            {
                Search = search,
                Landlords = landlords
            });
        }

        [HttpGet]
        [Authorize(Roles = "Admin,Landlord,Manager")]
        public async Task<IActionResult> Overview(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                TempData["Error"] = "User id is required.";
                return RedirectToAccessibleDirectory();
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
                return RedirectToAccessibleDirectory();
            }
        }

        [HttpGet]
        [Authorize(Roles = "Admin")]
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

        [HttpGet]
        [Authorize(Roles = "Admin")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> KycFile(string userId, string key)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(key))
            {
                return BadRequest("KYC file reference is required.");
            }

            var file = await _api.GetFileAsync(
                $"AdminUsers/{Uri.EscapeDataString(userId)}/kyc-file/{Uri.EscapeDataString(key)}");

            return File(file.Bytes, file.ContentType, file.FileName);
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveKyc(string userId, string? note = null, string? returnTo = null, string? search = null)
        {
            return await ReviewKyc(userId, approve: true, note, returnTo, search);
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectKyc(
            string userId,
            string? note = null,
            string? returnTo = null,
            string? search = null,
            bool rejectAllFiles = false,
            bool rejectFaceFront = false,
            bool rejectFaceRight = false,
            bool rejectFaceLeft = false,
            bool rejectDocumentFront = false,
            bool rejectDocumentBack = false)
        {
            return await ReviewKyc(
                userId,
                approve: false,
                note,
                returnTo,
                search,
                rejectAllFiles,
                rejectFaceFront,
                rejectFaceRight,
                rejectFaceLeft,
                rejectDocumentFront,
                rejectDocumentBack);
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RestartValidation(string userId, string? search = null, string? returnTo = null)
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

            return string.Equals(returnTo, "landlords", StringComparison.OrdinalIgnoreCase)
                ? RedirectToAction(nameof(Landlords), new { search })
                : RedirectToAction(nameof(Index), new { search });
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteUser(string userId, string? search = null, string? returnTo = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(userId))
                {
                    TempData["Error"] = "User id is required.";
                    return string.Equals(returnTo, "landlords", StringComparison.OrdinalIgnoreCase)
                        ? RedirectToAction(nameof(Landlords), new { search })
                        : RedirectToAction(nameof(Index), new { search });
                }

                await _api.DeleteAsync($"AdminUsers/{Uri.EscapeDataString(userId)}");
                TempData["Success"] = "User and related records were deleted.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = ExtractMessage(ex.Message);
            }

            return string.Equals(returnTo, "landlords", StringComparison.OrdinalIgnoreCase)
                ? RedirectToAction(nameof(Landlords), new { search })
                : RedirectToAction(nameof(Index), new { search });
        }

        private async Task<IActionResult> ReviewKyc(
            string userId,
            bool approve,
            string? note,
            string? returnTo,
            string? search,
            bool rejectAllFiles = false,
            bool rejectFaceFront = false,
            bool rejectFaceRight = false,
            bool rejectFaceLeft = false,
            bool rejectDocumentFront = false,
            bool rejectDocumentBack = false)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                TempData["Error"] = "User id is required.";
                return RedirectToAction(nameof(LandlordApprovals), new { search });
            }

            try
            {
                var endpoint = approve ? "approve" : "reject";
                await _api.PostAsync($"AdminUsers/{Uri.EscapeDataString(userId)}/kyc/{endpoint}", new KycReviewRequest
                {
                    Note = note,
                    RejectAllFiles = rejectAllFiles,
                    RejectFaceFront = rejectFaceFront,
                    RejectFaceRight = rejectFaceRight,
                    RejectFaceLeft = rejectFaceLeft,
                    RejectDocumentFront = rejectDocumentFront,
                    RejectDocumentBack = rejectDocumentBack
                });

                TempData["Success"] = approve
                    ? "Landlord identity verification approved."
                    : "Landlord identity verification rejected.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = ExtractMessage(ex.Message);
            }

            if (string.Equals(returnTo, "overview", StringComparison.OrdinalIgnoreCase))
            {
                return RedirectToAction(nameof(Overview), new { userId });
            }

            return RedirectToAction(nameof(LandlordApprovals), new { search });
        }

        private IActionResult RedirectToAccessibleDirectory()
        {
            return User.IsInRole("Admin")
                ? RedirectToAction(nameof(Index))
                : RedirectToAction("Index", "Members");
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
