using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Common.CommunicationModels;
using Common.Enums;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.Properties;

namespace RentHub.Portal.Controllers
{
    [Authorize(Roles = "Landlord")]
    public class PropertiesController : Controller
    {
        private readonly RentHubApiClient _api;

        public PropertiesController(RentHubApiClient api)
        {
            _api = api;
        }

        public async Task<IActionResult> Index(string? search = null)
        {
            var list = await _api.GetAsync<List<PropertyDto>>("properties/mine");
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim().ToLower();
                list = list.Where(p => (p.Name ?? "").ToLower().Contains(s)
                                     || (p.City ?? "").ToLower().Contains(s)
                                     || (p.Address ?? "").ToLower().Contains(s))
                           .ToList();
            }

            return View(new PropertyIndexVm { Search = search, Items = list });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(CreatePropertyRequest request)
        {
            await _api.PostAsync("properties", request);
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Overview(int id, string? apartmentSearch = null)
        {
            // ✅ one single call
            var overview = await _api.GetAsync<PropertyOverviewDto>($"properties/{id}/overview");

            // Filter apartments client-side
            if (!string.IsNullOrWhiteSpace(apartmentSearch) && overview.Property.Apartments != null)
            {
                var s = apartmentSearch.Trim().ToLower();
                overview.Property.Apartments = overview.Property.Apartments
                    .Where(a => (a.Name ?? "").ToLower().Contains(s))
                    .ToList();
            }

            return View(new PropertyOverviewVm
            {
                Property = overview.Property,
                Managers = overview.Managers,
                Documents = overview.Documents,
                ApartmentSearch = apartmentSearch,
                CanWrite = overview.CanWrite
            });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> AddApartment(CreateApartmentRequest request)
        {
            await _api.PostAsync("apartments", request);
            return RedirectToAction(nameof(Overview), new { id = request.PropertyId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> AddManager(int propertyId, AddManagerRequest request)
        {
            await _api.PostAsync($"properties/{propertyId}/managers", request);
            return RedirectToAction(nameof(Overview), new { id = propertyId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateManagerPermission(int propertyId, int assignmentId, PermissionLevelEnum permission)
        {
            await _api.PutAsync($"properties/{propertyId}/managers/{assignmentId}", permission);
            return RedirectToAction(nameof(Overview), new { id = propertyId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveManager(int propertyId, int assignmentId)
        {
            await _api.DeleteAsync($"properties/{propertyId}/managers/{assignmentId}");
            return RedirectToAction(nameof(Overview), new { id = propertyId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadPropertyDocument(int propertyId, IFormFile file, DocumentTypeEnum type)
        {
            if (file == null || file.Length == 0)
                return RedirectToAction(nameof(Overview), new { id = propertyId });

            var content = new MultipartFormDataContent();
            content.Add(new StringContent(type.ToString()), "Type");
            content.Add(new StreamContent(file.OpenReadStream()), "File", file.FileName);

            await _api.PostMultipartAsync<DocumentDto>($"documents/property/{propertyId}", content);
            return RedirectToAction(nameof(Overview), new { id = propertyId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteDocument(int propertyId, int documentId)
        {
            await _api.DeleteAsync($"documents/{documentId}");
            return RedirectToAction(nameof(Overview), new { id = propertyId });
        }
    }
}
