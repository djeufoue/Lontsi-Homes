using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Common.CommunicationModels;
using Common.Enums;
using RentHub.Portal.Helpers;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.Apartments;

namespace RentHub.Portal.Controllers
{
    [Authorize]
    public class ApartmentsController : Controller
    {
        private readonly RentHubApiClient _api;

        public ApartmentsController(RentHubApiClient api)
        {
            _api = api;
        }

        [Authorize(Roles = "Landlord")]
        public async Task<IActionResult> Index(string? search = null)
        {
            var items = await _api.GetAsync<List<ApartmentDto>>("apartments/mine");

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim().ToLower();
                items = items
                    .Where(a => (a.Name ?? "").ToLower().Contains(s)
                             || (a.PropertyName ?? "").ToLower().Contains(s))
                    .ToList();
            }

            return View(new ApartmentIndexVm
            {
                Search = search,
                Items = items
            });
        }

        public async Task<IActionResult> Overview(int id, string? tenancySearch = null)
        {
            var overview = await _api.GetAsync<ApartmentOverviewDto>($"apartments/{id}/overview");
            SuccessDialogHelper.ActivateForProperty(HttpContext.Session, overview.Apartment.PropertyId);

            if (!string.IsNullOrWhiteSpace(tenancySearch))
            {
                var s = tenancySearch.Trim().ToLower();
                overview.Tenancies = overview.Tenancies
                    .Where(t => (t.PropertyName ?? "").ToLower().Contains(s)
                             || (t.ApartmentName ?? "").ToLower().Contains(s))
                    .ToList();
            }

            return View(new ApartmentOverviewVm
            {
                ApartmentId = overview.Apartment.Id,
                ApartmentName = overview.Apartment.Name,
                PropertyName = overview.Apartment.PropertyName,
                Tenancies = overview.Tenancies,
                Owners = overview.Owners,
                Documents = overview.Documents,
                TenancySearch = tenancySearch
            });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateTenancy(CreateTenancyRequest request)
        {
            await _api.PostAsync("tenancies", request);
            TempData["Success"] = "Tenancy created successfully.";
            return RedirectToAction(nameof(Overview), new { id = request.ApartmentId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignOwner(int apartmentId, AssignApartmentOwnerRequest request)
        {
            await _api.PostAsync($"apartments/{apartmentId}/owners", request);
            TempData["Success"] = "Owner assigned successfully.";
            return RedirectToAction(nameof(Overview), new { id = apartmentId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateOwnerPermission(int apartmentId, int assignmentId, PermissionLevelEnum permission)
        {
            await _api.PutAsync($"apartments/{apartmentId}/owners/{assignmentId}", permission);
            TempData["Success"] = "Owner permission updated.";
            return RedirectToAction(nameof(Overview), new { id = apartmentId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveOwner(int apartmentId, int assignmentId)
        {
            await _api.DeleteAsync($"apartments/{apartmentId}/owners/{assignmentId}");
            TempData["Success"] = "Owner removed.";
            return RedirectToAction(nameof(Overview), new { id = apartmentId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadApartmentDocument(int apartmentId, IFormFile file, DocumentTypeEnum documentType, string? description)
        {
            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please choose a file to upload.";
                return RedirectToAction(nameof(Overview), new { id = apartmentId });
            }

            var content = new MultipartFormDataContent();
            content.Add(new StringContent(documentType.ToString()), "DocumentType");

            if (!string.IsNullOrWhiteSpace(description))
                content.Add(new StringContent(description), "Description");

            content.Add(new StreamContent(file.OpenReadStream()), "File", file.FileName);

            await _api.PostMultipartAsync<DocumentDto>($"documents/apartment/{apartmentId}", content);
            TempData["Success"] = "Apartment document uploaded.";
            return RedirectToAction(nameof(Overview), new { id = apartmentId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteDocument(int apartmentId, int documentId)
        {
            await _api.DeleteAsync($"documents/{documentId}");
            TempData["Success"] = "Apartment document deleted.";
            return RedirectToAction(nameof(Overview), new { id = apartmentId });
        }
    }
}
