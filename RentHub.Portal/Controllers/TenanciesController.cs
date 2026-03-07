using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Common.CommunicationModels;
using Common.Enums;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.Tenancies;

namespace RentHub.Portal.Controllers
{
    [Authorize]
    public class TenanciesController : Controller
    {
        private readonly RentHubApiClient _api;

        public TenanciesController(RentHubApiClient api)
        {
            _api = api;
        }

        public async Task<IActionResult> Index(string? search = null)
        {
            var items = await _api.GetAsync<List<TenancyDto>>("tenancies");

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim().ToLower();
                items = items.Where(t =>
                    (t.PropertyName ?? "").ToLower().Contains(s) ||
                    (t.ApartmentName ?? "").ToLower().Contains(s)
                ).ToList();
            }

            return View(new TenancyIndexVm
            {
                Search = search,
                Items = items
            });
        }

        public async Task<IActionResult> Overview(int id)
        {
            var overview = await _api.GetAsync<TenancyOverviewDto>($"tenancies/{id}/overview");

            return View(new TenancyOverviewVm
            {
                Tenancy = overview.Tenancy,
                Members = overview.Members,
                Documents = overview.Documents
            });
        }

        // -------- Members --------

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> AddMember(int tenancyId, AddTenancyMemberRequest request)
        {
            await _api.PostAsync($"tenancies/{tenancyId}/members", request);
            return RedirectToAction(nameof(Overview), new { id = tenancyId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveMember(int tenancyId, int memberId)
        {
            await _api.DeleteAsync($"tenancies/{tenancyId}/members/{memberId}");
            return RedirectToAction(nameof(Overview), new { id = tenancyId });
        }

        // -------- Tenancy settings --------

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateMaxMembers(int tenancyId, int maxMembers)
        {
            await _api.PutAsync($"tenancies/{tenancyId}/max-members", maxMembers);
            return RedirectToAction(nameof(Overview), new { id = tenancyId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Extend(int tenancyId, ExtendTenancyRequest request)
        {
            await _api.PutAsync($"tenancies/{tenancyId}/extend", request);
            return RedirectToAction(nameof(Overview), new { id = tenancyId });
        }

        // -------- Documents --------

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadTenancyDocument(int tenancyId, IFormFile file, DocumentTypeEnum documentType, string? description)
        {
            if (file == null || file.Length == 0)
                return RedirectToAction(nameof(Overview), new { id = tenancyId });

            var content = new MultipartFormDataContent();
            content.Add(new StringContent(documentType.ToString()), "DocumentType");

            if (!string.IsNullOrWhiteSpace(description))
                content.Add(new StringContent(description), "Description");

            content.Add(new StreamContent(file.OpenReadStream()), "File", file.FileName);

            await _api.PostMultipartAsync<DocumentDto>($"documents/tenancy/{tenancyId}", content);
            return RedirectToAction(nameof(Overview), new { id = tenancyId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteDocument(int tenancyId, int documentId)
        {
            await _api.DeleteAsync($"documents/{documentId}");
            return RedirectToAction(nameof(Overview), new { id = tenancyId });
        }
    }
}

