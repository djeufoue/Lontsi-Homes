using Common.CommunicationModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentHub.Portal.Services;
using RentHub.Portal.ViewModels.Conversations;
using System.Text.Json;

namespace RentHub.Portal.Controllers
{
    [Authorize(Roles = "Visitor,Landlord")]
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
        public async Task<IActionResult> Index(int? conversationId = null)
        {
            try
            {
                var items = await _api.GetAsync<List<ConversationListItemDto>>("Conversations/mine");
                ConversationThreadDto? selectedConversation = null;

                if (conversationId.HasValue)
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
                    SelectedConversation = selectedConversation,
                    SelectedConversationId = conversationId,
                    IsVisitor = User.IsInRole("Visitor"),
                    IsLandlord = User.IsInRole("Landlord")
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
