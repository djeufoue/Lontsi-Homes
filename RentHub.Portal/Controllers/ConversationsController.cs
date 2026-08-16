using Common.CommunicationModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.Conversations;
using System.Text.Json;

namespace RentHub.Portal.Controllers
{
    [Authorize(Roles = "Visitor,Landlord,Admin")]
    public class ConversationsController : Controller
    {
        private readonly RentHubApiClient _api;
        private readonly ILogger<ConversationsController> _logger;

        public ConversationsController(RentHubApiClient api, ILogger<ConversationsController> logger)
        {
            _api = api;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Index(int? conversationId = null, string? kind = null)
        {
            try
            {
                var isVisitor = User.IsInRole("Visitor");
                var isLandlord = User.IsInRole("Landlord");
                var isAdmin = User.IsInRole("Admin");
                var items = (isVisitor || isLandlord)
                    ? await _api.GetAsync<List<ConversationListItemDto>>("Conversations/mine")
                    : new List<ConversationListItemDto>();
                var subscriptionItems = (isLandlord || isAdmin)
                    ? await _api.GetAsync<List<SubscriptionInquiryListItemDto>>("subscription-inquiries/mine")
                    : new List<SubscriptionInquiryListItemDto>();
                ConversationThreadDto? selectedConversation = null;
                SubscriptionInquiryThreadDto? selectedSubscription = null;
                var selectedKind = string.Equals(kind, "subscription", StringComparison.OrdinalIgnoreCase) || isAdmin ? "subscription" : "apartment";

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
                    IsLandlord = isLandlord,
                    IsAdmin = isAdmin
                };

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
        [Authorize(Roles = "Visitor")]
        public async Task<IActionResult> Start(int apartmentId, string initialMessage, string? returnUrl = null)
        {
            try
            {
                await _api.PostAsync("Conversations/apartment/" + apartmentId + "/start", new StartConversationRequest
                {
                    InitialMessage = initialMessage
                });

                TempData["Success"] = "Private conversation started.";
                if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                {
                    return Redirect(returnUrl);
                }

                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to start conversation for apartment {ApartmentId}.", apartmentId);
                TempData["Error"] = ExtractSafeMessage(ex.Message, "We couldn't start the conversation right now.");
                if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                {
                    return Redirect(returnUrl);
                }

                return RedirectToAction("Apartment", "Home", new { id = apartmentId });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendMessage(int conversationId, string message, string? returnUrl = null)
        {
            try
            {
                await _api.PostAsync($"Conversations/{conversationId}/messages", new CreateConversationMessageRequest
                {
                    Message = message
                });

                TempData["Success"] = "Message sent.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to send message for conversation {ConversationId}.", conversationId);
                TempData["Error"] = ExtractSafeMessage(ex.Message, "We couldn't send your message right now.");
            }

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }

            return RedirectToAction(nameof(Index), new { conversationId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Landlord,Admin")]
        public async Task<IActionResult> SendSubscriptionMessage(int conversationId, string message)
        {
            try
            {
                await _api.PostAsync($"subscription-inquiries/{conversationId}/messages", new CreateSubscriptionInquiryMessageRequest { Message = message });
                TempData["Success"] = "Reply sent.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to send subscription inquiry reply {InquiryId}.", conversationId);
                TempData["Error"] = ExtractSafeMessage(ex.Message, "We couldn't send your reply right now.");
            }
            return RedirectToAction(nameof(Index), new { kind = "subscription", conversationId });
        }

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
