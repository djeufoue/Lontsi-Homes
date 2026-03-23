using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Common.CommunicationModels;
using Common.Enums;
using RentHub.Portal.Helpers;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.Apartments;
using System.Text.Json;

namespace RentHub.Portal.Controllers
{
    [Authorize]
    public class ApartmentsController : Controller
    {
        private readonly RentHubApiClient _api;
        private readonly ILogger<ApartmentsController> _logger;

        public ApartmentsController(RentHubApiClient api, ILogger<ApartmentsController> logger)
        {
            _api = api;
            _logger = logger;
        }

        [Authorize(Roles = "Landlord")]
        public async Task<IActionResult> Index(string? search = null)
        {
            try
            {
                var items = await _api.GetAsync<List<ApartmentDto>>("apartments/mine");

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var s = search.Trim().ToLowerInvariant();
                    items = items
                        .Where(a => (a.Name ?? string.Empty).ToLowerInvariant().Contains(s)
                                 || (a.PropertyName ?? string.Empty).ToLowerInvariant().Contains(s))
                        .ToList();
                }

                return View(new ApartmentIndexVm
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

        [HttpGet]
        public async Task<IActionResult> Overview(int id, string? tenancySearch = null, string? memberSearch = null)
        {
            try
            {
                var vm = await BuildOverviewVmAsync(id, tenancySearch, memberSearch);
                SuccessDialogHelper.ActivateForProperty(HttpContext.Session, vm.Apartment.PropertyId);
                return View(vm);
            }
            catch (Exception ex)
            {
                return await HandleApiFailureAsync(ex, RedirectToAction("Index", "Properties"));
            }
        }

        [HttpGet]
        public async Task<IActionResult> OverviewContent(int id, string? tenancySearch = null, string? memberSearch = null)
        {
            try
            {
                var vm = await BuildOverviewVmAsync(id, tenancySearch, memberSearch);
                return PartialView("Overview", vm);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Apartment overview content request failed in portal for apartment {ApartmentId}.", id);
                return Content("<div class=\"rh-empty\">Unable to reload the apartment workspace right now.</div>", "text/html");
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateTenancy(CreateTenancyVm request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    TempData["Error"] = "Please complete the tenancy details before saving.";
                    return await RedirectToApartmentOverviewAsync(request.ApartmentId);
                }

                if (request.ContractDocument != null && request.ContractDocument.Length > 0 && !IsPdf(request.ContractDocument))
                {
                    TempData["Error"] = "The tenancy contract must be a PDF file.";
                    return await RedirectToApartmentOverviewAsync(request.ApartmentId);
                }

                var tenancy = await _api.PostAsync<CreateTenancyRequest, TenancyDto>("tenancies", new CreateTenancyRequest
                {
                    ApartmentId = request.ApartmentId,
                    StartDate = request.StartDate,
                    EndDate = request.EndDate,
                    MonthlyRent = request.MonthlyRent,
                    MaxMembers = request.MaxMembers
                });

                if (request.ContractDocument != null && request.ContractDocument.Length > 0)
                {
                    var content = new MultipartFormDataContent();
                    content.Add(new StringContent(DocumentTypeEnum.TenancyContract.ToString()), "DocumentType");
                    content.Add(new StreamContent(request.ContractDocument.OpenReadStream()), "File", request.ContractDocument.FileName);

                    try
                    {
                        await _api.PostMultipartAsync<DocumentDto>($"documents/tenancy/{tenancy.Id}", content);
                        TempData["Success"] = "Tenancy created and contract uploaded successfully.";
                    }
                    catch (Exception uploadEx)
                    {
                        _logger.LogError(uploadEx, "Tenancy contract upload failed in portal for tenancy {TenancyId}.", tenancy.Id);
                        var uploadApiError = ParseApiError(uploadEx.Message);
                        TempData["Error"] = SafeUserMessage(uploadApiError.Message, "Tenancy was created, but the contract PDF could not be uploaded.");
                    }
                }
                else
                {
                    TempData["Success"] = "Tenancy created successfully.";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Create tenancy request failed in portal for apartment {ApartmentId}.", request.ApartmentId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to create the tenancy right now. Please try again.");
            }

            return await RedirectToApartmentOverviewAsync(request.ApartmentId);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateTenancy(UpdateTenancyVm request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    TempData["Error"] = "Please complete the tenancy details before saving.";
                    return await RedirectToApartmentOverviewAsync(request.ApartmentId);
                }

                await _api.PutAsync($"tenancies/{request.TenancyId}", new UpdateTenancyRequest
                {
                    StartDate = request.StartDate,
                    EndDate = request.EndDate,
                    MonthlyRent = request.MonthlyRent,
                    MaxMembers = request.MaxMembers
                });

                TempData["Success"] = "Tenancy updated successfully.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update tenancy request failed in portal for apartment {ApartmentId} tenancy {TenancyId}.", request.ApartmentId, request.TenancyId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to update the tenancy right now. Please try again.");
            }

            return await RedirectToApartmentOverviewAsync(request.ApartmentId);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateReminderSettings(int apartmentId, UpdateApartmentReminderSettingsRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    TempData["Error"] = "Please provide valid reminder settings for this apartment.";
                    return await RedirectToApartmentOverviewAsync(apartmentId);
                }

                await _api.PutAsync($"apartments/{apartmentId}/reminder-settings", request);
                TempData["Success"] = "Apartment reminder settings updated.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update apartment reminder settings failed in portal for apartment {ApartmentId}.", apartmentId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to update the apartment reminder settings right now. Please try again.");
            }

            return await RedirectToApartmentOverviewAsync(apartmentId);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignOwner(int apartmentId, AssignApartmentOwnerRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    TempData["Error"] = "Please complete the apartment member details before saving.";
                    return await RedirectToApartmentOverviewAsync(apartmentId);
                }

                await _api.PostAsync($"apartments/{apartmentId}/owners", request);
                TempData["Success"] = "Apartment member added successfully.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Assign apartment member request failed in portal for apartment {ApartmentId}.", apartmentId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to add the apartment member right now. Please try again.");
            }

            return await RedirectToApartmentOverviewAsync(apartmentId);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateOwnerPermission(int apartmentId, int assignmentId, UpdateApartmentMemberRequest request)
        {
            try
            {
                await _api.PutAsync($"apartments/{apartmentId}/owners/{assignmentId}", request);
                TempData["Success"] = "Apartment member updated.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update apartment member failed in portal for apartment {ApartmentId} assignment {AssignmentId}.", apartmentId, assignmentId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to update the apartment member right now. Please try again.");
            }

            return await RedirectToApartmentOverviewAsync(apartmentId);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveOwner(int apartmentId, int assignmentId)
        {
            try
            {
                await _api.DeleteAsync($"apartments/{apartmentId}/owners/{assignmentId}");
                TempData["Success"] = "Apartment member removed.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Remove apartment member failed in portal for apartment {ApartmentId} assignment {AssignmentId}.", apartmentId, assignmentId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to remove the apartment member right now. Please try again.");
            }

            return await RedirectToApartmentOverviewAsync(apartmentId);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadApartmentDocument(int apartmentId, IFormFile file, DocumentTypeEnum documentType)
        {
            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please choose a PDF file to upload.";
                return await RedirectToApartmentOverviewAsync(apartmentId);
            }

            if (!IsPdf(file))
            {
                TempData["Error"] = "Only PDF files are allowed in the apartment document section.";
                return await RedirectToApartmentOverviewAsync(apartmentId);
            }

            try
            {
                var content = new MultipartFormDataContent();
                content.Add(new StringContent(documentType.ToString()), "DocumentType");
                content.Add(new StreamContent(file.OpenReadStream()), "File", file.FileName);

                await _api.PostMultipartAsync<DocumentDto>($"documents/apartment/{apartmentId}", content);
                TempData["Success"] = "Apartment document uploaded.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upload apartment document request failed in portal for apartment {ApartmentId}.", apartmentId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to upload the apartment document right now. Please try again.");
            }

            return await RedirectToApartmentOverviewAsync(apartmentId);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadApartmentImages(int apartmentId, List<IFormFile> files)
        {
            files ??= new List<IFormFile>();
            var validFiles = files.Where(file => file != null && file.Length > 0).ToList();
            if (!validFiles.Any())
            {
                TempData["Error"] = "Please choose one or more image files to upload.";
                return await RedirectToApartmentOverviewAsync(apartmentId);
            }

            if (validFiles.Any(file => !IsImage(file)))
            {
                TempData["Error"] = "Only image files are allowed in the apartment image gallery.";
                return await RedirectToApartmentOverviewAsync(apartmentId);
            }

            try
            {
                foreach (var image in validFiles)
                {
                    var content = new MultipartFormDataContent();
                    content.Add(new StringContent(DocumentTypeEnum.ApartmentImage.ToString()), "DocumentType");
                    content.Add(new StreamContent(image.OpenReadStream()), "File", image.FileName);

                    await _api.PostMultipartAsync<DocumentDto>($"documents/apartment/{apartmentId}", content);
                }

                TempData["Success"] = validFiles.Count == 1
                    ? "Apartment image uploaded."
                    : $"{validFiles.Count} apartment images uploaded.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upload apartment images request failed in portal for apartment {ApartmentId}.", apartmentId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to upload apartment images right now. Please try again.");
            }

            return await RedirectToApartmentOverviewAsync(apartmentId);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadApartmentImage(int apartmentId, IFormFile file, int? currentDocumentId = null)
        {
            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please choose an image file to upload.";
                return await RedirectToApartmentOverviewAsync(apartmentId);
            }

            if (!IsImage(file))
            {
                TempData["Error"] = "Only image files are allowed in the apartment image gallery.";
                return await RedirectToApartmentOverviewAsync(apartmentId);
            }

            try
            {
                var replacedExisting = currentDocumentId.HasValue && currentDocumentId.Value > 0;
                var content = new MultipartFormDataContent();
                content.Add(new StringContent(DocumentTypeEnum.ApartmentImage.ToString()), "DocumentType");
                content.Add(new StreamContent(file.OpenReadStream()), "File", file.FileName);

                await _api.PostMultipartAsync<DocumentDto>($"documents/apartment/{apartmentId}", content);

                if (replacedExisting)
                {
                    try
                    {
                        await _api.DeleteAsync($"documents/{currentDocumentId.Value}");
                    }
                    catch (Exception deleteEx)
                    {
                        _logger.LogWarning(deleteEx, "Apartment image replacement uploaded but old image delete failed for apartment {ApartmentId} document {DocumentId}.", apartmentId, currentDocumentId.Value);
                    }
                }

                TempData["Success"] = replacedExisting
                    ? "Apartment image updated."
                    : "Apartment image uploaded.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upload apartment image request failed in portal for apartment {ApartmentId}.", apartmentId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to update the apartment image right now. Please try again.");
            }

            return await RedirectToApartmentOverviewAsync(apartmentId);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteDocument(int apartmentId, int documentId)
        {
            try
            {
                await _api.DeleteAsync($"documents/{documentId}");
                TempData["Success"] = "Apartment document deleted.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delete apartment document request failed in portal for apartment {ApartmentId} document {DocumentId}.", apartmentId, documentId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to delete the apartment document right now. Please try again.");
            }

            return await RedirectToApartmentOverviewAsync(apartmentId);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteApartmentImage(int apartmentId, int documentId)
        {
            try
            {
                await _api.DeleteAsync($"documents/{documentId}");
                TempData["Success"] = "Apartment image deleted.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delete apartment image request failed in portal for apartment {ApartmentId} document {DocumentId}.", apartmentId, documentId);
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to delete the apartment image right now. Please try again.");
            }

            return await RedirectToApartmentOverviewAsync(apartmentId);
        }

        private async Task<ApartmentOverviewVm> BuildOverviewVmAsync(int id, string? tenancySearch, string? memberSearch)
        {
            var overview = await _api.GetAsync<ApartmentOverviewDto>($"apartments/{id}/overview");

            var tenancies = overview.Tenancies ?? new List<TenancyDto>();
            if (!string.IsNullOrWhiteSpace(tenancySearch))
            {
                var search = tenancySearch.Trim().ToLowerInvariant();
                tenancies = tenancies
                    .Where(t => (t.PropertyName ?? string.Empty).ToLowerInvariant().Contains(search)
                             || (t.ApartmentName ?? string.Empty).ToLowerInvariant().Contains(search))
                    .ToList();
            }

            var owners = overview.Owners ?? new List<ApartmentOwnerDto>();
            if (!string.IsNullOrWhiteSpace(memberSearch))
            {
                var search = memberSearch.Trim().ToLowerInvariant();
                owners = owners
                    .Where(o => (o.OwnerName ?? string.Empty).ToLowerInvariant().Contains(search)
                             || o.Permission.ToString().ToLowerInvariant().Contains(search)
                             || o.Role.ToString().ToLowerInvariant().Contains(search))
                    .ToList();
            }

            return new ApartmentOverviewVm
            {
                Apartment = overview.Apartment,
                Tenancies = tenancies,
                Owners = owners,
                Documents = overview.Documents ?? new List<DocumentDto>(),
                TenancySearch = tenancySearch,
                MemberSearch = memberSearch,
                CanWrite = overview.Apartment.CanWrite
            };
        }

        private async Task<IActionResult> RedirectToApartmentOverviewAsync(int apartmentId)
        {
            var overview = await _api.GetAsync<ApartmentOverviewDto>($"apartments/{apartmentId}/overview");
            SuccessDialogHelper.ActivateForProperty(HttpContext.Session, overview.Apartment.PropertyId);
            return RedirectToAction(nameof(Overview), new { id = apartmentId });
        }

        private Task<IActionResult> HandleApiFailureAsync(Exception ex, IActionResult? fallback = null)
        {
            _logger.LogError(ex, "Apartment request failed in portal.");

            var apiError = ParseApiError(ex.Message);
            TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to load the apartment workspace right now. Please try again.");
            return Task.FromResult<IActionResult>(fallback ?? RedirectToAction("Index", "Properties"));
        }

        private static bool IsPdf(IFormFile file)
        {
            var contentType = file.ContentType ?? string.Empty;
            var extension = Path.GetExtension(file.FileName ?? string.Empty);

            return string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsImage(IFormFile file)
        {
            var contentType = file.ContentType ?? string.Empty;
            var extension = Path.GetExtension(file.FileName ?? string.Empty);

            return contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" }
                    .Contains(extension, StringComparer.OrdinalIgnoreCase);
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

