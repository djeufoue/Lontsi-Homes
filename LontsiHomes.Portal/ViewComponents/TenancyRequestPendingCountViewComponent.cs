using Common.CommunicationModels;
using Microsoft.AspNetCore.Mvc;
using LontsiHomes.Portal.Services;

namespace LontsiHomes.Portal.ViewComponents
{
    public sealed class TenancyRequestPendingCountViewComponent : ViewComponent
    {
        private readonly LontsiHomesApiClient _api;

        public TenancyRequestPendingCountViewComponent(LontsiHomesApiClient api)
        {
            _api = api;
        }

        public async Task<IViewComponentResult> InvokeAsync()
        {
            if (User.Identity?.IsAuthenticated != true)
            {
                return Content(string.Empty);
            }

            try
            {
                var result = await _api.GetAsync<TenancyRequestPendingCountDto>("tenancy-requests/pending-count");
                return View(result);
            }
            catch
            {
                // A failed counter must never make the navigation unavailable.
                return View(new TenancyRequestPendingCountDto());
            }
        }
    }
}
