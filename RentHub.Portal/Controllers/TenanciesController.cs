using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Common.CommunicationModels;
using Common.Enums;
using RentHub.Portal.Helpers;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.Tenancies;
using System.Text.Json;

namespace RentHub.Portal.Controllers
{
    [Authorize]
    public class TenanciesController : Controller
    {
        private readonly RentHubApiClient _api;
        private readonly ILogger<TenanciesController> _logger;

        public TenanciesController(RentHubApiClient api, ILogger<TenanciesController> logger)
        {
            _api = api;
            _logger = logger;
        }

        public async Task<IActionResult> Index(string? search = null)
        {
            try
            {
                var items = await _api.GetAsync<List<TenancyDto>>("tenancies");

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var s = search.Trim().ToLowerInvariant();
                    items = items.Where(t =>
                        (t.PropertyName ?? string.Empty).ToLowerInvariant().Contains(s) ||
                        (t.ApartmentName ?? string.Empty).ToLowerInvariant().Contains(s))
                        .ToList();
                }

                return View(new TenancyIndexVm
                {
                    Search = search,
                    Items = items
                });
            }
            catch (Exception ex)
            {
                return await HandleApiFailureAsync(ex, RedirectToAction("Index", "Properties"));
            }
        }

        public async Task<IActionResult> Overview(int id, string? memberSearch = null)
        {
            try
            {
                var vm = await BuildOverviewVmAsync(id, memberSearch);
                SuccessDialogHelper.ActivateForProperty(HttpContext.Session, vm.Tenancy.PropertyId);
                return View(vm);
            }
            catch (Exception ex)
            {
                return await HandleApiFailureAsync(ex, RedirectToAction("Index", "Properties"));
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> AddMember(int tenancyId, AddTenancyMemberRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    TempData["Error"] = "Please complete the tenancy member details before saving.";
                    return RedirectToAction(nameof(Overview), new { id = tenancyId });
                }

                await _api.PostAsync($"tenancies/{tenancyId}/members", request);
                TempData["Success"] = "Tenancy member added.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Add tenancy member request failed in portal for tenancy {TenancyId}.", tenancyId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to add the tenancy member right now. Please try again.");
            }

            return RedirectToAction(nameof(Overview), new { id = tenancyId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateMemberRole(int tenancyId, int memberId, UpdateTenancyMemberRequest request)
        {
            try
            {
                await _api.PutAsync($"tenancies/{tenancyId}/members/{memberId}", request);
                TempData["Success"] = "Tenancy member updated.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update tenancy member failed in portal for tenancy {TenancyId} member {MemberId}.", tenancyId, memberId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to update the tenancy member right now. Please try again.");
            }

            return RedirectToAction(nameof(Overview), new { id = tenancyId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveMember(int tenancyId, int memberId)
        {
            try
            {
                await _api.DeleteAsync($"tenancies/{tenancyId}/members/{memberId}");
                TempData["Success"] = "Tenancy member removed.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Remove tenancy member failed in portal for tenancy {TenancyId} member {MemberId}.", tenancyId, memberId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to remove the tenancy member right now. Please try again.");
            }

            return RedirectToAction(nameof(Overview), new { id = tenancyId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadContract(int tenancyId, IFormFile file, int? currentDocumentId = null)
        {
            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please choose a tenancy contract PDF to upload.";
                return RedirectToAction(nameof(Overview), new { id = tenancyId });
            }

            if (!IsPdf(file))
            {
                TempData["Error"] = "Only PDF files are allowed for the tenancy contract.";
                return RedirectToAction(nameof(Overview), new { id = tenancyId });
            }

            try
            {
                var existingOverview = await _api.GetAsync<TenancyOverviewDto>($"tenancies/{tenancyId}/overview");
                var existingContractIds = existingOverview.Documents
                    .Where(doc => doc.DocumentType == DocumentTypeEnum.TenancyContract)
                    .Select(doc => doc.Id)
                    .ToList();

                var content = new MultipartFormDataContent();
                content.Add(new StringContent(DocumentTypeEnum.TenancyContract.ToString()), "DocumentType");
                content.Add(new StreamContent(file.OpenReadStream()), "File", file.FileName);

                var createdDocument = await _api.PostMultipartAsync<DocumentDto>($"documents/tenancy/{tenancyId}", content);

                foreach (var documentId in existingContractIds.Where(id => !currentDocumentId.HasValue || id != createdDocument.Id))
                {
                    try
                    {
                        await _api.DeleteAsync($"documents/{documentId}");
                    }
                    catch (Exception deleteEx)
                    {
                        _logger.LogWarning(deleteEx, "Tenancy contract cleanup failed in portal for tenancy {TenancyId} document {DocumentId}.", tenancyId, documentId);
                    }
                }

                TempData["Success"] = existingContractIds.Any() ? "Tenancy contract updated." : "Tenancy contract uploaded.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upload tenancy contract request failed in portal for tenancy {TenancyId}.", tenancyId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to upload the tenancy contract right now. Please try again.");
            }

            return RedirectToAction(nameof(Overview), new { id = tenancyId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteDocument(int tenancyId, int documentId)
        {
            try
            {
                await _api.DeleteAsync($"documents/{documentId}");
                TempData["Success"] = "Tenancy contract deleted.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delete tenancy contract failed in portal for tenancy {TenancyId} document {DocumentId}.", tenancyId, documentId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to delete the tenancy contract right now. Please try again.");
            }

            return RedirectToAction(nameof(Overview), new { id = tenancyId });
        }

        private async Task<TenancyOverviewVm> BuildOverviewVmAsync(int id, string? memberSearch)
        {
            var overview = await _api.GetAsync<TenancyOverviewDto>($"tenancies/{id}/overview");
            var members = overview.Members ?? new List<TenancyMemberDto>();
            if (!string.IsNullOrWhiteSpace(memberSearch))
            {
                var search = memberSearch.Trim().ToLowerInvariant();
                members = members
                    .Where(m => (m.FullName ?? string.Empty).ToLowerInvariant().Contains(search)
                             || (m.Email ?? string.Empty).ToLowerInvariant().Contains(search)
                             || (m.Role ?? string.Empty).ToLowerInvariant().Contains(search))
                    .ToList();
            }

            return new TenancyOverviewVm
            {
                Tenancy = overview.Tenancy,
                Members = members,
                Documents = (overview.Documents ?? new List<DocumentDto>())
                    .Where(doc => doc.DocumentType == DocumentTypeEnum.TenancyContract)
                    .OrderByDescending(doc => doc.UploadedAt)
                    .ToList(),
                MemberSearch = memberSearch
            };
        }

        private Task<IActionResult> HandleApiFailureAsync(Exception ex, IActionResult? fallback = null)
        {
            _logger.LogError(ex, "Tenancy request failed in portal.");

            var apiError = ParseApiError(ex.Message);
            TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to load the tenancy workspace right now. Please try again.");
            return Task.FromResult<IActionResult>(fallback ?? RedirectToAction("Index", "Properties"));
        }

        private static bool IsPdf(IFormFile file)
        {
            var contentType = file.ContentType ?? string.Empty;
            var extension = Path.GetExtension(file.FileName ?? string.Empty);

            return string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);
        }

        private static ApiErrorPayload ParseApiError(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new ApiErrorPayload();

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.String)
                {
                    return new ApiErrorPayload { Message = root.GetString() };
                }

                string? ReadString(string key)
                {
                    if (root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty(key, out var value) &&
                        value.ValueKind == JsonValueKind.String)
                    {
                        return value.GetString();
                    }

                    return null;
                }

                return new ApiErrorPayload
                {
                    Code = ReadString("Code") ?? ReadString("code"),
                    Message = ReadString("Message") ?? ReadString("message")
                };
            }
            catch
            {
                return new ApiErrorPayload { Message = raw };
            }
        }

        private static string SafeUserMessage(string? apiMessage, string fallback)
        {
            if (string.IsNullOrWhiteSpace(apiMessage))
            {
                return fallback;
            }

            return LooksTechnicalMessage(apiMessage) ? fallback : apiMessage;
        }

        private static bool LooksTechnicalMessage(string message)
        {
            var normalized = message.Trim();
            if (normalized.Length == 0)
            {
                return true;
            }

            var technicalFragments = new[]
            {
                "exception",
                "stack trace",
                "inner exception",
                "dbupdateexception",
                "sqlexception",
                "invalid column name",
                "entity changes",
                "microsoft.entityframeworkcore",
                " at "
            };

            return technicalFragments.Any(fragment =>
                normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }

        private sealed class ApiErrorPayload
        {
            public string? Code { get; set; }
            public string? Message { get; set; }
        }
    }
}
