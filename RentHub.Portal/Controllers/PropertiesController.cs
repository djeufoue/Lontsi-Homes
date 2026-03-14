using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Common.CommunicationModels;
using Common.Enums;
using RentHub.Portal.Helpers;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.Documents;
using RentHub.Portal.ViewModels.Properties;
using System.IO;
using System.Text.Json;

namespace RentHub.Portal.Controllers
{
    [Authorize]
    public class PropertiesController : Controller
    {
        private const long PropertyImageMaxBytes = 2 * 1024 * 1024;

        private readonly RentHubApiClient _api;
        private readonly ILogger<PropertiesController> _logger;

        public PropertiesController(RentHubApiClient api, ILogger<PropertiesController> logger)
        {
            _api = api;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? search = null, string? city = null, string? access = null, int page = 1, int pageSize = 12)
        {
            try
            {
                var vm = await BuildIndexVm(search, city, access, page, pageSize);
                return View(vm);
            }
            catch (Exception ex)
            {
                return await HandleApiFailureAsync(ex);
            }
        }

        [HttpGet]
        public async Task<IActionResult> ListPartial(string? search = null, string? city = null, string? access = null, int page = 1, int pageSize = 12)
        {
            try
            {
                var vm = await BuildIndexVm(search, city, access, page, pageSize);
                return PartialView("_PropertyGrid", vm);
            }
            catch (Exception ex)
            {
                var fallback = Content("<div class=\"rh-empty\">Unable to load properties right now.</div>", "text/html");
                return await HandleApiFailureAsync(ex, fallback);
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            CreatePropertyVm vm,
            string? returnSearch = null,
            string? returnCity = null,
            string? returnAccess = null,
            int returnPage = 1,
            int returnPageSize = 12)
        {
            if (!ModelState.IsValid)
            {
                var missingFields = ModelState
                    .Where(kvp => kvp.Value?.Errors.Count > 0)
                    .Select(kvp => kvp.Key)
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Select(key => key.Replace("vm.", string.Empty))
                    .Distinct()
                    .ToList();

                TempData["Error"] = missingFields.Count > 0
                    ? $"Please complete required fields: {string.Join(", ", missingFields)}."
                    : "Please complete all required property fields.";

                return RedirectToAction(nameof(Index), new
                {
                    search = returnSearch,
                    city = returnCity,
                    access = returnAccess,
                    page = returnPage,
                    pageSize = returnPageSize
                });
            }

            try
            {
                var request = new CreatePropertyRequest
                {
                    Name = vm.Name,
                    City = vm.City,
                    Address = vm.Address,
                    Description = vm.Description,
                    LandlordId = vm.LandlordId
                };

                await _api.PostAsync("properties", request);
                TempData["Success"] = "Property created successfully.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Create property request failed in portal.");
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to create property right now. Please try again.");
            }

            return RedirectToAction(nameof(Index), new
            {
                search = returnSearch,
                city = returnCity,
                access = returnAccess,
                page = returnPage,
                pageSize = returnPageSize
            });
        }

        [HttpGet]
        public async Task<IActionResult> Overview(int id, string? apartmentSearch = null, string? memberSearch = null, int unitsPage = 1, int unitsPageSize = 6)
        {
            try
            {
                var vm = await BuildPropertyOverviewVmAsync(id, apartmentSearch, memberSearch, unitsPage, unitsPageSize);
                return View(vm);
            }
            catch (Exception ex)
            {
                return await HandleApiFailureAsync(ex, RedirectToAction(nameof(Index)));
            }
        }

        [HttpGet]
        public async Task<IActionResult> Map(int id)
        {
            try
            {
                var vm = await BuildPropertyOverviewVmAsync(id, null, null, 1, 6);
                return View(vm);
            }
            catch (Exception ex)
            {
                return await HandleApiFailureAsync(ex, RedirectToAction(nameof(Index)));
            }
        }

        [HttpGet]
        public async Task<IActionResult> DocumentPreview(int id)
        {
            try
            {
                var vm = await BuildDocumentPreviewVmAsync(id);
                return View(vm);
            }
            catch (Exception ex)
            {
                return await HandleApiFailureAsync(ex, RedirectToAction(nameof(Index)));
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateApartment(CreateApartmentVm vm)
        {
            if (!ModelState.IsValid)
            {
                TempData["Error"] = "Please complete the apartment details before saving.";
                SuccessDialogHelper.ActivateForProperty(HttpContext.Session, vm.PropertyId);
                return RedirectToAction(nameof(Overview), new { id = vm.PropertyId });
            }

            try
            {
                var request = new CreateApartmentRequest
                {
                    PropertyId = vm.PropertyId,
                    Name = vm.Name,
                    Type = vm.Type,
                    Price = vm.Price,
                    Area = vm.Area
                };

                await _api.PostAsync("apartments", request);
                TempData["Success"] = "Apartment added successfully.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Create apartment request failed in portal.");
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to add the apartment right now. Please try again.");
            }

            SuccessDialogHelper.ActivateForProperty(HttpContext.Session, vm.PropertyId);
            return RedirectToAction(nameof(Overview), new { id = vm.PropertyId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult UpdateSuccessDialogSettings(int propertyId, bool showCloseButton, int autoCloseSeconds)
        {
            SuccessDialogHelper.SaveForProperty(HttpContext.Session, propertyId, showCloseButton, autoCloseSeconds);
            TempData["Success"] = "Success dialog settings updated for this property.";
            return RedirectToAction(nameof(Overview), new { id = propertyId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateSettings(int propertyId, UpdatePropertyRequest request)
        {
            try
            {
                await _api.PutAsync($"properties/{propertyId}", request);
                TempData["Success"] = "Property settings updated.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update settings request failed in portal.");
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to update property settings right now. Please try again.");
            }

            SuccessDialogHelper.ActivateForProperty(HttpContext.Session, propertyId);
            return RedirectToAction(nameof(Overview), new { id = propertyId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddMember(int propertyId, AddManagerVm vm, string role = "Manager")
        {
            if (!ModelState.IsValid)
            {
                TempData["Error"] = "Please provide valid member details.";
                SuccessDialogHelper.ActivateForProperty(HttpContext.Session, propertyId);
                return RedirectToAction(nameof(Overview), new { id = propertyId });
            }

            if (!string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Only Manager role is currently supported at property level.";
                SuccessDialogHelper.ActivateForProperty(HttpContext.Session, propertyId);
                return RedirectToAction(nameof(Overview), new { id = propertyId });
            }

            try
            {
                var request = new AddManagerRequest
                {
                    Email = vm.Email,
                    FullName = vm.FullName,
                    CountryCode = vm.CountryCode ?? string.Empty,
                    Permission = vm.Permission
                };

                await _api.PostAsync($"properties/{propertyId}/managers", request);
                TempData["Success"] = "Property member added successfully.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Add property member request failed in portal.");
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to add the property member right now. Please try again.");
            }

            SuccessDialogHelper.ActivateForProperty(HttpContext.Session, propertyId);
            return RedirectToAction(nameof(Overview), new { id = propertyId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateManagerPermission(int propertyId, int assignmentId, PermissionLevelEnum permission)
        {
            try
            {
                await _api.PutAsync($"properties/{propertyId}/managers/{assignmentId}", permission);
                TempData["Success"] = "Member access updated.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update member permission request failed in portal.");
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to update member access right now. Please try again.");
            }

            SuccessDialogHelper.ActivateForProperty(HttpContext.Session, propertyId);
            return RedirectToAction(nameof(Overview), new { id = propertyId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveManager(int propertyId, int assignmentId)
        {
            try
            {
                await _api.DeleteAsync($"properties/{propertyId}/managers/{assignmentId}");
                TempData["Success"] = "Property member removed.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Remove property member request failed in portal.");
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to remove the property member right now. Please try again.");
            }

            SuccessDialogHelper.ActivateForProperty(HttpContext.Session, propertyId);
            return RedirectToAction(nameof(Overview), new { id = propertyId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadPropertyDocument(int propertyId, IFormFile file, DocumentTypeEnum type)
        {
            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please choose a file to upload.";
                SuccessDialogHelper.ActivateForProperty(HttpContext.Session, propertyId);
                return RedirectToAction(nameof(Overview), new { id = propertyId });
            }

            if (type == DocumentTypeEnum.PropertyImage)
            {
                if (file.Length > PropertyImageMaxBytes)
                {
                    TempData["Error"] = "Property images must be 2 MB or smaller.";
                    SuccessDialogHelper.ActivateForProperty(HttpContext.Session, propertyId);
                    return RedirectToAction(nameof(Overview), new { id = propertyId });
                }

                if (string.IsNullOrWhiteSpace(file.ContentType) || !file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    TempData["Error"] = "Please upload a valid image file for the property image.";
                    SuccessDialogHelper.ActivateForProperty(HttpContext.Session, propertyId);
                    return RedirectToAction(nameof(Overview), new { id = propertyId });
                }
            }

            try
            {
                var content = new MultipartFormDataContent();
                content.Add(new StringContent(type.ToString()), "Type");
                content.Add(new StreamContent(file.OpenReadStream()), "File", file.FileName);

                await _api.PostMultipartAsync<DocumentDto>($"documents/property/{propertyId}", content);
                TempData["Success"] = type == DocumentTypeEnum.PropertyImage
                    ? "Property image uploaded."
                    : "Document uploaded.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upload property file request failed in portal.");
                var apiError = ParseApiError(ex.Message);
                var uploadFallback = type == DocumentTypeEnum.PropertyImage
                    ? "Unable to upload the property image right now. Please try again."
                    : "Unable to upload the property document right now. Please try again.";
                TempData["Error"] = SafeUserMessage(apiError.Message, uploadFallback);
            }

            SuccessDialogHelper.ActivateForProperty(HttpContext.Session, propertyId);
            return RedirectToAction(nameof(Overview), new { id = propertyId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteDocument(int propertyId, int documentId)
        {
            try
            {
                await _api.DeleteAsync($"documents/{documentId}");
                TempData["Success"] = "Document deleted.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delete property document request failed in portal.");
                var apiError = ParseApiError(ex.Message);
                TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to delete the property document right now. Please try again.");
            }

            SuccessDialogHelper.ActivateForProperty(HttpContext.Session, propertyId);
            return RedirectToAction(nameof(Overview), new { id = propertyId });
        }

        private async Task<IActionResult> HandleApiFailureAsync(Exception ex, IActionResult? fallback = null)
        {
            _logger.LogError(ex, "Properties request failed in portal.");

            var apiError = ParseApiError(ex.Message);
            if (string.Equals(apiError.Code, "AUTH_SESSION_EXPIRED", StringComparison.OrdinalIgnoreCase))
            {
                HttpContext.Session.Remove("JWT_TOKEN");
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                TempData["Error"] = "Your session expired. Please sign in again.";
                return RedirectToAction("Login", "Auth");
            }

            TempData["Error"] = SafeUserMessage(apiError.Message, "Unable to load properties right now. Please try again.");
            return fallback ?? RedirectToAction("Index", "Home");
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

        private async Task<PropertyOverviewVm> BuildPropertyOverviewVmAsync(int id, string? apartmentSearch, string? memberSearch, int unitsPage, int unitsPageSize)
        {
            var overview = await _api.GetAsync<PropertyOverviewDto>($"properties/{id}/overview");

            SuccessDialogHelper.ActivateForProperty(HttpContext.Session, id);
            var dialogSettings = SuccessDialogHelper.GetForProperty(HttpContext.Session, id);

            var apartments = overview.Property.Apartments?.ToList() ?? new List<ApartmentDto>();
            if (!string.IsNullOrWhiteSpace(apartmentSearch))
            {
                var search = apartmentSearch.Trim().ToLowerInvariant();
                apartments = apartments.Where(a =>
                    (a.Name ?? string.Empty).ToLowerInvariant().Contains(search) ||
                    (a.Type ?? string.Empty).ToLowerInvariant().Contains(search) ||
                    (a.Status ?? string.Empty).ToLowerInvariant().Contains(search)).ToList();
            }

            var managers = overview.Managers?.ToList() ?? new List<PropertyManagerDto>();
            if (!string.IsNullOrWhiteSpace(memberSearch))
            {
                var search = memberSearch.Trim().ToLowerInvariant();
                managers = managers.Where(m =>
                    (m.ManagerName ?? string.Empty).ToLowerInvariant().Contains(search) ||
                    m.Permission.ToString().ToLowerInvariant().Contains(search)).ToList();
            }

            unitsPage = unitsPage < 1 ? 1 : unitsPage;
            unitsPageSize = unitsPageSize < 1 ? 6 : Math.Min(unitsPageSize, 50);

            var totalUnits = apartments.Count;
            var pagedUnits = apartments
                .Skip((unitsPage - 1) * unitsPageSize)
                .Take(unitsPageSize)
                .ToList();

            return new PropertyOverviewVm
            {
                Property = overview.Property,
                Managers = overview.Managers ?? new List<PropertyManagerDto>(),
                FilteredManagers = managers,
                Documents = overview.Documents ?? new List<DocumentDto>(),
                ApartmentSearch = apartmentSearch,
                MemberSearch = memberSearch,
                CanWrite = overview.CanWrite,
                UnitsPage = unitsPage,
                UnitsPageSize = unitsPageSize,
                TotalUnits = totalUnits,
                Units = pagedUnits,
                SuccessDialogShowCloseButton = dialogSettings.ShowCloseButton,
                SuccessDialogAutoCloseSeconds = dialogSettings.AutoCloseSeconds
            };
        }

        private async Task<DocumentPreviewVm> BuildDocumentPreviewVmAsync(int id)
        {
            var document = await _api.GetAsync<DocumentDto>($"documents/{id}");

            string propertyName = string.Empty;
            string? apartmentName = null;
            string? tenancyName = null;
            int? propertyId = document.PropertyId;
            int? apartmentId = document.ApartmentId;
            int? tenancyId = document.TenancyId;

            if (document.PropertyId.HasValue)
            {
                var propertyOverview = await _api.GetAsync<PropertyOverviewDto>($"properties/{document.PropertyId.Value}/overview");
                propertyName = propertyOverview.Property.Name;
                propertyId = propertyOverview.Property.Id;
            }

            if (document.ApartmentId.HasValue)
            {
                var apartmentOverview = await _api.GetAsync<ApartmentOverviewDto>($"apartments/{document.ApartmentId.Value}/overview");
                apartmentName = apartmentOverview.Apartment.Name;
                propertyName = string.IsNullOrWhiteSpace(propertyName) ? apartmentOverview.Apartment.PropertyName : propertyName;
                propertyId ??= apartmentOverview.Apartment.PropertyId;
                apartmentId = apartmentOverview.Apartment.Id;
            }

            if (document.TenancyId.HasValue)
            {
                var tenancyOverview = await _api.GetAsync<TenancyOverviewDto>($"tenancies/{document.TenancyId.Value}/overview");
                tenancyName = $"Tenancy #{tenancyOverview.Tenancy.Id}";
                apartmentName ??= tenancyOverview.Tenancy.ApartmentName;
                propertyName = string.IsNullOrWhiteSpace(propertyName) ? tenancyOverview.Tenancy.PropertyName : propertyName;
                propertyId ??= tenancyOverview.Tenancy.PropertyId;
                apartmentId ??= tenancyOverview.Tenancy.ApartmentId;
                tenancyId = tenancyOverview.Tenancy.Id;
            }

            var extension = Path.GetExtension(document.FileName ?? string.Empty).ToLowerInvariant();
            var isImage = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".svg" }.Contains(extension)
                || document.DocumentType == DocumentTypeEnum.PropertyImage
                || document.DocumentType == DocumentTypeEnum.ApartmentImage;
            var isPdf = extension == ".pdf";

            var sectionLabel = tenancyId.HasValue
                ? "Tenancy document"
                : apartmentId.HasValue
                    ? "Apartment document"
                    : "Property document";

            return new DocumentPreviewVm
            {
                Document = document,
                PropertyId = propertyId,
                PropertyName = propertyName,
                ApartmentId = apartmentId,
                ApartmentName = apartmentName,
                TenancyId = tenancyId,
                TenancyName = tenancyName,
                SectionLabel = sectionLabel,
                IsImage = isImage,
                IsPdf = isPdf
            };
        }

        private async Task<PropertyIndexVm> BuildIndexVm(string? search, string? city, string? access, int page, int pageSize)
        {
            page = page < 1 ? 1 : page;
            pageSize = pageSize < 1 ? 12 : Math.Min(pageSize, 50);

            string E(string? value) => Uri.EscapeDataString(value ?? string.Empty);

            var endpoint = $"properties/dashboard?search={E(search)}&city={E(city)}&access={E(access)}&page={page}&pageSize={pageSize}";
            var response = await _api.GetAsync<PropertyListResponseDto>(endpoint);

            return new PropertyIndexVm
            {
                Search = search,
                City = city,
                Access = access,
                Page = response.Page,
                PageSize = response.PageSize,
                TotalCount = response.TotalCount,
                UserRole = response.UserRole,
                CanCreateProperty = response.CanCreateProperty,
                CreationScopes = response.CreationScopes,
                Items = response.Items
            };
        }
    }
}

