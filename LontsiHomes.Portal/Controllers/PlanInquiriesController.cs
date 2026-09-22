using System.Security.Claims;
using Common.CommunicationModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LontsiHomes.Portal.Services;
using LontsiHomes.Portal.ViewModels.PlanInquiries;

namespace LontsiHomes.Portal.Controllers
{
    public class PlanInquiriesController : Controller
    {
        private readonly LontsiHomesApiClient _api;
        public PlanInquiriesController(LontsiHomesApiClient api) => _api = api;

        [HttpGet, AllowAnonymous]
        public IActionResult Index(string? plan = null)
        {
            return View(new PlanInquiryVm
            {
                PlanName = string.IsNullOrWhiteSpace(plan) ? "Enterprise Unlimited" : plan.Trim(),
                RequesterName = User.FindFirstValue(ClaimTypes.Name),
                RequesterEmail = User.FindFirstValue(ClaimTypes.Email),
                IsAuthenticated = User.Identity?.IsAuthenticated == true
            });
        }

        [HttpPost, AllowAnonymous, ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(PlanInquiryVm model)
        {
            model.IsAuthenticated = User.Identity?.IsAuthenticated == true;
            if (!model.IsAuthenticated && string.IsNullOrWhiteSpace(model.RequesterName)) ModelState.AddModelError(nameof(model.RequesterName), "Your name is required.");
            if (!model.IsAuthenticated && string.IsNullOrWhiteSpace(model.RequesterEmail)) ModelState.AddModelError(nameof(model.RequesterEmail), "A valid email address is required.");
            if (!ModelState.IsValid) return View("Index", model);

            var request = new CreateSubscriptionInquiryRequest
            {
                PlanName = model.PlanName,
                PropertyCount = model.PropertyCount,
                ApartmentCount = model.ApartmentCount,
                TenantCount = model.TenantCount,
                ProposedMonthlyPrice = model.ProposedMonthlyPrice,
                CommitmentMonths = model.CommitmentMonths!.Value,
                RequesterName = model.RequesterName,
                RequesterEmail = model.RequesterEmail,
                Message = model.Message
            };

            try
            {
                var created = model.IsAuthenticated
                    ? await _api.PostAsync<CreateSubscriptionInquiryRequest, SubscriptionInquiryCreatedDto>("subscription-inquiries", request)
                    : await _api.PostAnonymousAsync<CreateSubscriptionInquiryRequest, SubscriptionInquiryCreatedDto>("subscription-inquiries", request);
                TempData["Success"] = "Your plan request was sent. An administrator will reply in Conversations and by email.";
                return model.IsAuthenticated
                    ? RedirectToAction("Index", "Conversations", new { kind = "subscription", conversationId = created.InquiryId })
                    : RedirectToAction(nameof(Thread), new { token = created.PublicAccessToken });
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                return View("Index", model);
            }
        }

        [HttpGet, AllowAnonymous]
        public async Task<IActionResult> Thread(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return NotFound();
            var thread = await _api.GetAnonymousAsync<SubscriptionInquiryThreadDto>($"subscription-inquiries/public/{Uri.EscapeDataString(token)}");
            return View(new PublicPlanInquiryThreadVm { Token = token, Thread = thread });
        }

        [HttpPost, AllowAnonymous, ValidateAntiForgeryToken]
        public async Task<IActionResult> Reply(string token, string message)
        {
            await _api.PostAnonymousAsync($"subscription-inquiries/public/{Uri.EscapeDataString(token)}/messages", new CreateSubscriptionInquiryMessageRequest { Message = message });
            TempData["Success"] = "Reply sent.";
            return RedirectToAction(nameof(Thread), new { token });
        }
    }
}
