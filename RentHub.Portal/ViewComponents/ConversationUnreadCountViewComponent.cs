using Common.CommunicationModels;
using Microsoft.AspNetCore.Mvc;
using RentHub.Portal.Services;

namespace RentHub.Portal.ViewComponents
{
    public sealed class ConversationUnreadCountViewComponent : ViewComponent
    {
        private readonly RentHubApiClient _api;

        public ConversationUnreadCountViewComponent(RentHubApiClient api)
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
                var result = await _api.GetAsync<ConversationUnreadCountDto>("Conversations/unread-count");
                return View(result.UnreadCount);
            }
            catch
            {
                // Navigation must remain available even if the counter cannot be refreshed.
                return Content(string.Empty);
            }
        }
    }
}
