using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentHub.Portal.Models;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.Auth;
using RentHub.Portal.ViewModels.Home;
using System.Diagnostics;

namespace RentHub.Portal.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly RentHubApiClient _api;

        public HomeController(ILogger<HomeController> logger, RentHubApiClient api)
        {
            _logger = logger;
            _api = api;
        }

        [AllowAnonymous]
        public async Task<IActionResult> Index(string? search = null, string? city = null, string? status = null, int page = 1, int pageSize = 12)
        {
            var vm = new PublicHomeIndexVm
            {
                IsAuthenticated = User.Identity?.IsAuthenticated == true,
                IsLandlordOperator = User.IsInRole("Landlord") || User.IsInRole("Manager") || User.IsInRole("Admin")
            };

            try
            {
                vm.Plans = await _api.GetAsync<List<SubscriptionPlanOptionVm>>("Subscriptions/plans");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to load public home data.");
                TempData["Error"] = "We couldn't load the landlord onboarding information right now. Please refresh in a moment.";
            }

            return View(vm);
        }

        [AllowAnonymous]
        [HttpGet("apartments/{id:int}")]
        public IActionResult Apartment(int id)
        {
            TempData["Error"] = "The public apartment detail page is temporarily hidden while we focus on the first release.";
            return RedirectToAction(nameof(Index));
        }

        [AllowAnonymous]
        public IActionResult Privacy()
        {
            return View();
        }

        [AllowAnonymous]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
