using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentHub.Portal.Models;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.Auth;
using RentHub.Portal.ViewModels.Home;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;

namespace RentHub.Portal.Controllers
{
    public class HomeController : Controller
    {
        private static readonly TimeSpan[] PublicPlansStartupRetryDelays =
        {
            TimeSpan.FromMilliseconds(400),
            TimeSpan.FromMilliseconds(900),
            TimeSpan.FromMilliseconds(1500)
        };

        private readonly ILogger<HomeController> _logger;
        private readonly RentHubApiClient _api;
        private readonly IConfiguration _configuration;

        public HomeController(ILogger<HomeController> logger, RentHubApiClient api, IConfiguration configuration)
        {
            _logger = logger;
            _api = api;
            _configuration = configuration;
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
                vm.Plans = await LoadPublicSubscriptionPlansAsync();
            }
            catch (HttpRequestException ex) when (IsApiStillStarting(ex))
            {
                _logger.LogWarning(ex, "Public subscription plans are unavailable while the API is starting.");
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Timed out while loading public subscription plans.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to load public home data.");
                TempData["Error"] = "We couldn't load the landlord onboarding information right now. Please refresh in a moment.";
            }

            return View(vm);
        }

        private async Task<List<SubscriptionPlanOptionVm>> LoadPublicSubscriptionPlansAsync()
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await _api.GetAnonymousAsync<List<SubscriptionPlanOptionVm>>("Subscriptions/plans");
                }
                catch (HttpRequestException ex) when (IsApiStillStarting(ex) && attempt < PublicPlansStartupRetryDelays.Length)
                {
                    await Task.Delay(PublicPlansStartupRetryDelays[attempt]);
                }
                catch (TaskCanceledException) when (attempt < PublicPlansStartupRetryDelays.Length)
                {
                    await Task.Delay(PublicPlansStartupRetryDelays[attempt]);
                }
            }
        }

        private static bool IsApiStillStarting(HttpRequestException ex)
        {
            return ex.GetBaseException() is SocketException socketException &&
                   socketException.SocketErrorCode == SocketError.ConnectionRefused;
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
            return View(BuildPublicSiteInfo());
        }

        [AllowAnonymous]
        public IActionResult Terms()
        {
            return View(BuildPublicSiteInfo());
        }

        [AllowAnonymous]
        public IActionResult RefundPolicy()
        {
            return View(BuildPublicSiteInfo());
        }

        [AllowAnonymous]
        public IActionResult Contact()
        {
            return View(BuildPublicSiteInfo());
        }

        [AllowAnonymous]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }

        private PublicSiteInfoVm BuildPublicSiteInfo()
        {
            return new PublicSiteInfoVm
            {
                SiteName = _configuration["PublicSite:SiteName"] ?? "Lontsi Homes",
                LegalEntityName = _configuration["PublicSite:LegalEntityName"] ?? "Lontsi Homes",
                SupportEmail = _configuration["PublicSite:SupportEmail"] ?? "REMOVED_PRIVATE_VALUE",
                RefundEmail = _configuration["PublicSite:RefundEmail"] ?? "REMOVED_PRIVATE_VALUE",
                SupportPhone = _configuration["PublicSite:SupportPhone"] ?? "REMOVED_PRIVATE_VALUE",
                SupportWhatsApp = _configuration["PublicSite:SupportWhatsApp"] ?? "REMOVED_PRIVATE_VALUE",
                CompanyAddress = _configuration["PublicSite:CompanyAddress"] ?? "London, Ontario, Canada"
            };
        }
    }
}
