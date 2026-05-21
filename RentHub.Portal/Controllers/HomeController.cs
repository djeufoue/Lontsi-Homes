using Common.CommunicationModels;
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
                Search = search,
                City = city,
                Status = status,
                Page = page,
                PageSize = pageSize,
                IsAuthenticated = User.Identity?.IsAuthenticated == true,
                IsVisitor = User.IsInRole("Visitor"),
                IsLandlordOperator = User.IsInRole("Landlord") || User.IsInRole("Manager") || User.IsInRole("Admin"),
                CanOpenMessages = User.IsInRole("Visitor") || User.IsInRole("Landlord")
            };

            try
            {
                string E(string? value) => Uri.EscapeDataString(value ?? string.Empty);
                var apartmentsTask = _api.GetAnonymousAsync<PublicApartmentCatalogResponseDto>(
                    $"public/apartments?search={E(search)}&city={E(city)}&status={E(status)}&page={page}&pageSize={pageSize}");
                var plansTask = _api.GetAnonymousAsync<List<SubscriptionPlanOptionVm>>("Subscriptions/plans");
                await Task.WhenAll(apartmentsTask, plansTask);

                var apartments = apartmentsTask.Result;
                vm.Page = apartments.Page;
                vm.PageSize = apartments.PageSize;
                vm.TotalCount = apartments.TotalCount;
                vm.Apartments = apartments.Items;
                vm.Plans = plansTask.Result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to load public home data.");
                TempData["Error"] = "We couldn't load the public apartment showcase right now. Please refresh in a moment.";
            }

            return View(vm);
        }

        [AllowAnonymous]
        [HttpGet("apartments/{id:int}")]
        public async Task<IActionResult> Apartment(int id)
        {
            try
            {
                var apartment = await _api.GetAnonymousAsync<PublicApartmentOverviewDto>($"public/apartments/{id}");
                ConversationThreadDto? conversation = null;

                if (User.Identity?.IsAuthenticated == true && User.IsInRole("Visitor"))
                {
                    try
                    {
                        conversation = await _api.GetAsync<ConversationThreadDto>($"Conversations/apartment/{id}/mine");
                    }
                    catch
                    {
                        conversation = null;
                    }
                }

                var returnUrl = $"{Request.Path}{Request.QueryString}";
                var vm = new PublicApartmentPageVm
                {
                    Apartment = apartment,
                    Conversation = conversation,
                    IsAuthenticated = User.Identity?.IsAuthenticated == true,
                    IsVisitor = User.IsInRole("Visitor"),
                    IsLandlord = User.IsInRole("Landlord"),
                    CanStartConversation = User.IsInRole("Visitor"),
                    LoginReturnUrl = returnUrl,
                    RegisterVisitorReturnUrl = returnUrl
                };

                return View(vm);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to load public apartment page for apartment {ApartmentId}.", id);
                TempData["Error"] = "We couldn't load this apartment right now.";
                return RedirectToAction(nameof(Index));
            }
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
