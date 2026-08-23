using Common.CommunicationModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using RentHub.Portal.Hubs;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.Conversations;
using System.Text.Json;

namespace RentHub.Portal.Controllers
{
    [Authorize(Roles = "Tenant,Visitor,Landlord,Manager,Admin")]
    public class ConversationsController : Controller
    {
        private readonly RentHubApiClient _api;
        private readonly ILogger<ConversationsController> _logger;
        private readonly IHubContext<WorkspaceHub> _hub;

        public ConversationsController(RentHubApiClient api, ILogger<ConversationsController> logger, IHubContext<WorkspaceHub> hub)
        {
            _api = api;
            _logger = logger;
            _hub = hub;
        }

        [HttpGet]
        public async Task<IActionResult> Index(
            int? conversationId = null,
            string? kind = null,
            int? propertyId = null,
            int? apartmentId = null,
            bool liveRefresh = false)
        {
            try
            {
                var isVisitor = User.IsInRole("Visitor");
                var isTenant = User.IsInRole("Tenant");
                var isLandlord = User.IsInRole("Landlord");
                var isManager = User.IsInRole("Manager");
                var isAdmin = User.IsInRole("Admin");
                var canUsePropertyMessaging = isVisitor || isTenant || isLandlord || isManager || isAdmin;
                var inboxUrl = "Conversations/mine";
                var query = new List<string>();
                if (propertyId.HasValue)
                {
                    query.Add($"propertyId={propertyId.Value}");
                }
                if (apartmentId.HasValue)
                {
                    query.Add($"apartmentId={apartmentId.Value}");
                }
                if (query.Count > 0)
                {
                    inboxUrl += "?" + string.Join("&", query);
                }

                var items = canUsePropertyMessaging
                    ? await _api.GetAsync<List<ConversationListItemDto>>(inboxUrl)
                    : new List<ConversationListItemDto>();
                var workspace = canUsePropertyMessaging
                    ? await _api.GetAsync<ConversationWorkspaceDto>("Conversations/workspace")
                    : new ConversationWorkspaceDto();
                var subscriptionItems = (isLandlord || isAdmin)
                    ? await _api.GetAsync<List<SubscriptionInquiryListItemDto>>("subscription-inquiries/mine")
                    : new List<SubscriptionInquiryListItemDto>();
                ConversationThreadDto? selectedConversation = null;
                SubscriptionInquiryThreadDto? selectedSubscription = null;
                var selectedKind = string.Equals(kind, "subscription", StringComparison.OrdinalIgnoreCase)
                    ? "subscription"
                    : string.Equals(kind, "team", StringComparison.OrdinalIgnoreCase) ? "team" : "apartment";
                if (string.IsNullOrWhiteSpace(kind) && isAdmin && subscriptionItems.Count > 0)
                {
                    selectedKind = "subscription";
                }

                if (selectedKind == "subscription" && conversationId.HasValue)
                {
                    selectedSubscription = await _api.GetAsync<SubscriptionInquiryThreadDto>($"subscription-inquiries/{conversationId.Value}");
                }
                else if (selectedKind == "subscription" && subscriptionItems.Count > 0)
                {
                    selectedSubscription = await _api.GetAsync<SubscriptionInquiryThreadDto>($"subscription-inquiries/{subscriptionItems[0].InquiryId}");
                    conversationId = selectedSubscription.InquiryId;
                }
                else if (conversationId.HasValue)
                {
                    selectedConversation = await _api.GetAsync<ConversationThreadDto>($"Conversations/{conversationId.Value}");
                }
                else if (items.Count > 0)
                {
                    selectedConversation = await _api.GetAsync<ConversationThreadDto>($"Conversations/{items[0].ConversationId}");
                    conversationId = selectedConversation.ConversationId;
                    selectedKind = selectedConversation.IsPropertyTeamConversation ? "team" : "apartment";
                }

                var vm = new ConversationInboxVm
                {
                    Items = items,
                    SubscriptionItems = subscriptionItems,
                    SelectedConversation = selectedConversation,
                    SelectedSubscriptionInquiry = selectedSubscription,
                    SelectedConversationId = conversationId,
                    SelectedKind = selectedKind,
                    IsVisitor = isVisitor,
                    IsTenant = isTenant,
                    IsLandlord = isLandlord,
                    IsManager = isManager,
                    IsAdmin = isAdmin,
                    SelectedPropertyId = propertyId,
                    SelectedApartmentId = apartmentId,
                    Workspace = workspace
                };

                if (!liveRefresh && conversationId.HasValue)
                {
                    await PublishConversationChangedAsync(conversationId.Value, selectedKind, "read");
                }
                return View(vm);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to load conversations inbox.");
                TempData["Error"] = "We couldn't load your conversations right now.";
                return RedirectToAction("Index", "Home");
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Visitor,Tenant")]
        public async Task<IActionResult> Start(int apartmentId, string initialMessage, string? returnUrl = null)
        {
            try
            {
                var conversation = await _api.PostAsync<StartConversationRequest, ConversationThreadDto>("Conversations/apartment/" + apartmentId + "/start", new StartConversationRequest
                {
                    InitialMessage = initialMessage
                });
                await PublishConversationChangedAsync(conversation.ConversationId, "apartment", "message");
                if (IsAjaxRequest()) return Json(new { success = true, conversationId = conversation.ConversationId, kind = "apartment" });
                if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                {
                    return Redirect(returnUrl);
                }

                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to start conversation for apartment {ApartmentId}.", apartmentId);
                var error = ExtractSafeMessage(ex.Message, "We couldn't start the conversation right now.");
                if (IsAjaxRequest()) return BadRequest(new { message = error });
                TempData["Error"] = error;
                if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                {
                    return Redirect(returnUrl);
                }

                return User.IsInRole("Tenant")
                    ? RedirectToAction(nameof(Index))
                    : RedirectToAction("Apartment", "Home", new { id = apartmentId });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendMessage(int conversationId, string message, int? replyToMessageId = null, string? returnUrl = null)
        {
            try
            {
                var conversation = await _api.PostAsync<CreateConversationMessageRequest, ConversationThreadDto>($"Conversations/{conversationId}/messages", new CreateConversationMessageRequest
                {
                    Message = message,
                    ReplyToMessageId = replyToMessageId
                });
                var kind = conversation.IsPropertyTeamConversation ? "team" : "apartment";
                await PublishConversationChangedAsync(conversationId, kind, "message");
                if (IsAjaxRequest()) return Json(new { success = true, conversationId, kind });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to send message for conversation {ConversationId}.", conversationId);
                var error = ExtractSafeMessage(ex.Message, "We couldn't send your message right now.");
                if (IsAjaxRequest()) return BadRequest(new { message = error });
                TempData["Error"] = error;
            }

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction(nameof(Index), new { conversationId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Landlord,Manager")]
        public async Task<IActionResult> Broadcast(int propertyId, string message, string? returnUrl = null)
        {
            try
            {
                await _api.PostAsync($"Conversations/property/{propertyId}/broadcast", new CreatePropertyBroadcastRequest
                {
                    Message = message
                });
                await PublishConversationChangedAsync(null, "apartment", "broadcast");
                if (IsAjaxRequest()) return Json(new { success = true, kind = "apartment" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to broadcast a conversation message for property {PropertyId}.", propertyId);
                var error = ExtractSafeMessage(ex.Message, "We couldn't send this property announcement right now.");
                if (IsAjaxRequest()) return BadRequest(new { message = error });
                TempData["Error"] = error;
            }

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction(nameof(Index), new { propertyId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Landlord,Manager,Admin")]
        public async Task<IActionResult> StartTeamConversation(int propertyId, string initialMessage)
        {
            try
            {
                var conversation = await _api.PostAsync<StartPropertyTeamConversationRequest, ConversationThreadDto>(
                    $"Conversations/property/{propertyId}/team/start",
                    new StartPropertyTeamConversationRequest { InitialMessage = initialMessage });
                await PublishConversationChangedAsync(conversation.ConversationId, "team", "message");
                if (IsAjaxRequest()) return Json(new { success = true, conversationId = conversation.ConversationId, kind = "team" });
                return RedirectToAction(nameof(Index), new { kind = "team", conversationId = conversation.ConversationId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to start internal conversation for property {PropertyId}.", propertyId);
                var error = ExtractSafeMessage(ex.Message, "We couldn't start the internal conversation right now.");
                if (IsAjaxRequest()) return BadRequest(new { message = error });
                TempData["Error"] = error;
                return RedirectToAction(nameof(Index));
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Landlord,Admin")]
        public async Task<IActionResult> SendSubscriptionMessage(int conversationId, string message)
        {
            try
            {
                await _api.PostAsync($"subscription-inquiries/{conversationId}/messages", new CreateSubscriptionInquiryMessageRequest { Message = message });
                await PublishConversationChangedAsync(conversationId, "subscription", "message");
                if (IsAjaxRequest()) return Json(new { success = true, conversationId, kind = "subscription" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to send subscription inquiry reply {InquiryId}.", conversationId);
                var error = ExtractSafeMessage(ex.Message, "We couldn't send your reply right now.");
                if (IsAjaxRequest()) return BadRequest(new { message = error });
                TempData["Error"] = error;
            }
            return RedirectToAction(nameof(Index), new { kind = "subscription", conversationId });
        }

        private bool IsAjaxRequest() => string.Equals(Request.Headers.XRequestedWith, "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

        private Task PublishConversationChangedAsync(int? conversationId, string kind, string changeType) =>
            _hub.Clients.All.SendAsync("ConversationsChanged", new
            {
                conversationId,
                kind,
                changeType,
                changedAt = DateTimeOffset.UtcNow
            });

        private static string ExtractSafeMessage(string raw, string fallback)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (raw.Contains("exception", StringComparison.OrdinalIgnoreCase))
            {
                return fallback;
            }

            try
            {
                using var document = JsonDocument.Parse(raw);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var name in new[] { "Message", "message" })
                    {
                        if (document.RootElement.TryGetProperty(name, out var value) &&
                            value.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(value.GetString()))
                        {
                            return value.GetString()!;
                        }
                    }
                }
            }
            catch
            {
            }

            return raw;
        }
    }
}
