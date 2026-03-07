using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Common.CommunicationModels;
using Common.Enums;
using RentHub.Portal.Helpers;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.Properties;

namespace RentHub.Portal.Controllers
{
    [Authorize]
    public class PropertiesController : Controller
    {
        private readonly RentHubApiClient _api;

        public PropertiesController(RentHubApiClient api)
        {
            _api = api;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? search = null, string? city = null, string? access = null, int page = 1, int pageSize = 12)
        {
            var vm = await BuildIndexVm(search, city, access, page, pageSize);
            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> ListPartial(string? search = null, string? city = null, string? access = null, int page = 1, int pageSize = 12)
        {
            var vm = await BuildIndexVm(search, city, access, page, pageSize);
            return PartialView("_PropertyGrid", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(CreatePropertyVm vm, string? search = null, string? city = null, string? access = null, int page = 1, int pageSize = 12)
        {
            if (!ModelState.IsValid)
            {
                TempData["Error"] = "Please complete all required property fields.";
                return RedirectToAction(nameof(Index), new { search, city, access, page, pageSize });
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
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction(nameof(Index), new { search, city, access, page, pageSize });
        }

        [HttpGet]
        public async Task<IActionResult> Overview(int id, string? apartmentSearch = null, int unitsPage = 1, int unitsPageSize = 6)
        {
            var overview = await _api.GetAsync<PropertyOverviewDto>($"properties/{id}/overview");

            SuccessDialogHelper.ActivateForProperty(HttpContext.Session, id);
            var dialogSettings = SuccessDialogHelper.GetForProperty(HttpContext.Session, id);

            var apartments = overview.Property.Apartments?.ToList() ?? new List<ApartmentDto>();
            if (!string.IsNullOrWhiteSpace(apartmentSearch))
            {
                var s = apartmentSearch.Trim().ToLower();
                apartments = apartments.Where(a =>
                    (a.Name ?? string.Empty).ToLower().Contains(s) ||
                    (a.Type ?? string.Empty).ToLower().Contains(s) ||
                    (a.Status ?? string.Empty).ToLower().Contains(s)).ToList();
            }

            unitsPage = unitsPage < 1 ? 1 : unitsPage;
            unitsPageSize = unitsPageSize < 1 ? 6 : Math.Min(unitsPageSize, 50);

            var totalUnits = apartments.Count;
            var pagedUnits = apartments
                .Skip((unitsPage - 1) * unitsPageSize)
                .Take(unitsPageSize)
                .ToList();

            return View(new PropertyOverviewVm
            {
                Property = overview.Property,
                Managers = overview.Managers,
                Documents = overview.Documents,
                ApartmentSearch = apartmentSearch,
                CanWrite = overview.CanWrite,
                UnitsPage = unitsPage,
                UnitsPageSize = unitsPageSize,
                TotalUnits = totalUnits,
                Units = pagedUnits,
                SuccessDialogShowCloseButton = dialogSettings.ShowCloseButton,
                SuccessDialogAutoCloseSeconds = dialogSettings.AutoCloseSeconds
            });
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
                TempData["Error"] = ex.Message;
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
                TempData["Error"] = ex.Message;
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
                TempData["Error"] = ex.Message;
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
                TempData["Error"] = ex.Message;
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

            try
            {
                var content = new MultipartFormDataContent();
                content.Add(new StringContent(type.ToString()), "Type");
                content.Add(new StreamContent(file.OpenReadStream()), "File", file.FileName);

                await _api.PostMultipartAsync<DocumentDto>($"documents/property/{propertyId}", content);
                TempData["Success"] = "Document uploaded.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
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
                TempData["Error"] = ex.Message;
            }

            SuccessDialogHelper.ActivateForProperty(HttpContext.Session, propertyId);
            return RedirectToAction(nameof(Overview), new { id = propertyId });
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
