using Common.CommunicationModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LontsiHomes.Portal.Services;

namespace LontsiHomes.Portal.Controllers;

[Authorize(Roles = "Admin,Landlord,Manager")]
public class MembersController : Controller
{
    private readonly LontsiHomesApiClient _api;
    private readonly ILogger<MembersController> _logger;

    public MembersController(LontsiHomesApiClient api, ILogger<MembersController> logger)
    {
        _api = api;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(
        string? search = null,
        int? propertyId = null,
        string? role = null,
        int page = 1,
        int pageSize = 12)
    {
        try
        {
            var endpoint = $"workspace-directory/members?search={Uri.EscapeDataString(search ?? string.Empty)}" +
                           $"&propertyId={propertyId}&role={Uri.EscapeDataString(role ?? string.Empty)}" +
                           $"&page={page}&pageSize={pageSize}";
            var model = await _api.GetAsync<WorkspaceDirectoryResponseDto<WorkspaceMemberDto>>(endpoint);
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the workspace member directory.");
            TempData["Error"] = "Unable to load members right now. Please try again.";
            return RedirectToAction("Index", "Properties");
        }
    }
}
