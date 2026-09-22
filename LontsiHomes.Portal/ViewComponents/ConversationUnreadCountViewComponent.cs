using Common.CommunicationModels;
using Microsoft.AspNetCore.Mvc;
using LontsiHomes.Portal.Services;

namespace LontsiHomes.Portal.ViewComponents
{
    public sealed class ConversationUnreadCountViewComponent : ViewComponent
    {
        private readonly LontsiHomesApiClient _api;

        public ConversationUnreadCountViewComponent(LontsiHomesApiClient api)
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
