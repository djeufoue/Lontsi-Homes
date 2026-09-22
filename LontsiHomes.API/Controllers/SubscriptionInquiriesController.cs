using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Common.CommunicationModels;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LontsiHomes.API.Data;
using LontsiHomes.API.Helpers;
using LontsiHomes.API.Models.Entities;
using LontsiHomes.API.Services.Conversations;

namespace LontsiHomes.API.Controllers
{
    [ApiController]
    [Route("api/subscription-inquiries")]
    public class SubscriptionInquiriesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IBackgroundJobClient _backgroundJobs;

        public SubscriptionInquiriesController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IBackgroundJobClient backgroundJobs)
        {
            _context = context;
            _userManager = userManager;
            _backgroundJobs = backgroundJobs;
        }

        [HttpPost]
        [AllowAnonymous]
        public async Task<IActionResult> Create(CreateSubscriptionInquiryRequest request)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            ApplicationUser? requester = null;
            var userId = UserHelpers.GetUserId(User);
            if (!string.IsNullOrWhiteSpace(userId)) requester = await _userManager.FindByIdAsync(userId);

            var email = requester?.Email?.Trim() ?? request.RequesterEmail?.Trim();
            var name = requester?.FullName?.Trim() ?? request.RequesterName?.Trim();
            if (string.IsNullOrWhiteSpace(email) || !new EmailAddressAttribute().IsValid(email))
                return BadRequest(new { Message = "A valid email address is required." });

            name = string.IsNullOrWhiteSpace(name) ? email : name;
            var now = DateTimeOffset.UtcNow;
            var inquiry = new SubscriptionInquiry
            {
                RequesterUserId = requester?.Id,
                RequesterName = name,
                RequesterEmail = email,
                PlanName = request.PlanName.Trim(),
                PropertyCount = request.PropertyCount,
                ApartmentCount = request.ApartmentCount,
                TenantCount = request.TenantCount,
                ProposedMonthlyPrice = request.ProposedMonthlyPrice,
                CommitmentMonths = request.CommitmentMonths,
                PublicAccessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                CreatedAt = now,
                LastMessageAt = now,
                RequesterLastReadAt = now
            };

            var initialMessage = new SubscriptionInquiryMessage
            {
                SenderUserId = requester?.Id,
                SenderName = name,
                SenderEmail = email,
                SentByAdmin = false,
                Body = request.Message.Trim(),
                CreatedAt = now
            };
            inquiry.Messages.Add(initialMessage);

            _context.SubscriptionInquiries.Add(inquiry);
            await _context.SaveChangesAsync();
            _backgroundJobs.Enqueue<IConversationNotificationJob>(job =>
                job.SendSubscriptionInquiryNotificationsAsync(initialMessage.Id));

            return Ok(new SubscriptionInquiryCreatedDto { InquiryId = inquiry.Id, PublicAccessToken = inquiry.PublicAccessToken });
        }

        [HttpGet("mine")]
        [Authorize(Roles = "Landlord,Admin")]
        public async Task<ActionResult<List<SubscriptionInquiryListItemDto>>> Mine()
        {
            var userId = UserHelpers.GetUserId(User);
            var isAdmin = User.IsInRole("Admin");
            var query = _context.SubscriptionInquiries.Include(i => i.Messages).AsQueryable();
            if (!isAdmin) query = query.Where(i => i.RequesterUserId == userId);

            var rows = await query.OrderByDescending(i => i.LastMessageAt).ToListAsync();
            return Ok(rows.Select(i => ToListItem(i, isAdmin)).ToList());
        }

        [HttpGet("{inquiryId:int}")]
        [Authorize(Roles = "Landlord,Admin")]
        public async Task<IActionResult> Get(int inquiryId)
        {
            var inquiry = await LoadAsync(inquiryId);
            if (inquiry == null) return NotFound();
            var userId = UserHelpers.GetUserId(User);
            var isAdmin = User.IsInRole("Admin");
            if (!isAdmin && !string.Equals(inquiry.RequesterUserId, userId, StringComparison.Ordinal)) return Forbid();

            if (isAdmin) inquiry.AdminLastReadAt = DateTimeOffset.UtcNow;
            else inquiry.RequesterLastReadAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync();
            return Ok(ToThread(inquiry, isAdmin, userId));
        }

        [HttpPost("{inquiryId:int}/messages")]
        [Authorize(Roles = "Landlord,Admin")]
        public async Task<IActionResult> Reply(int inquiryId, CreateSubscriptionInquiryMessageRequest request)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);
            var inquiry = await LoadAsync(inquiryId);
            if (inquiry == null) return NotFound();

            var userId = UserHelpers.GetUserId(User);
            var user = await _userManager.FindByIdAsync(userId ?? string.Empty);
            var isAdmin = User.IsInRole("Admin");
            if (user == null || (!isAdmin && !string.Equals(inquiry.RequesterUserId, userId, StringComparison.Ordinal))) return Forbid();

            await AddMessageAsync(inquiry, user.Id, user.FullName ?? user.Email ?? "User", user.Email ?? string.Empty, isAdmin, request.Message.Trim());
            return Ok(ToThread(inquiry, isAdmin, userId));
        }

        [HttpGet("public/{token}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetPublic(string token)
        {
            var inquiry = await LoadByTokenAsync(token);
            if (inquiry == null) return NotFound();
            inquiry.RequesterLastReadAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync();
            return Ok(ToThread(inquiry, false, inquiry.RequesterUserId));
        }

        [HttpPost("public/{token}/messages")]
        [AllowAnonymous]
        public async Task<IActionResult> ReplyPublic(string token, CreateSubscriptionInquiryMessageRequest request)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);
            var inquiry = await LoadByTokenAsync(token);
            if (inquiry == null) return NotFound();
            await AddMessageAsync(inquiry, inquiry.RequesterUserId, inquiry.RequesterName, inquiry.RequesterEmail, false, request.Message.Trim());
            return Ok(ToThread(inquiry, false, inquiry.RequesterUserId));
        }

        private async Task AddMessageAsync(SubscriptionInquiry inquiry, string? senderId, string senderName, string senderEmail, bool isAdmin, string body)
        {
            var now = DateTimeOffset.UtcNow;
            var message = new SubscriptionInquiryMessage
            {
                SenderUserId = senderId,
                SenderName = senderName,
                SenderEmail = senderEmail,
                SentByAdmin = isAdmin,
                Body = body,
                CreatedAt = now
            };
            inquiry.Messages.Add(message);
            inquiry.LastMessageAt = now;
            if (isAdmin) inquiry.AdminLastReadAt = now; else inquiry.RequesterLastReadAt = now;
            await _context.SaveChangesAsync();
            _backgroundJobs.Enqueue<IConversationNotificationJob>(job =>
                job.SendSubscriptionInquiryNotificationsAsync(message.Id));
        }

        private async Task<SubscriptionInquiry?> LoadAsync(int id) => await _context.SubscriptionInquiries.Include(i => i.Messages).FirstOrDefaultAsync(i => i.Id == id);
        private async Task<SubscriptionInquiry?> LoadByTokenAsync(string token) => await _context.SubscriptionInquiries.Include(i => i.Messages).FirstOrDefaultAsync(i => i.PublicAccessToken == token);

        private static SubscriptionInquiryListItemDto ToListItem(SubscriptionInquiry inquiry, bool isAdmin)
        {
            var latest = inquiry.Messages.OrderByDescending(m => m.CreatedAt).FirstOrDefault();
            var unread = isAdmin
                ? inquiry.Messages.Any(m => !m.SentByAdmin && (!inquiry.AdminLastReadAt.HasValue || m.CreatedAt > inquiry.AdminLastReadAt))
                : inquiry.Messages.Any(m => m.SentByAdmin && (!inquiry.RequesterLastReadAt.HasValue || m.CreatedAt > inquiry.RequesterLastReadAt));
            return new SubscriptionInquiryListItemDto { InquiryId = inquiry.Id, PlanName = inquiry.PlanName, RequesterName = inquiry.RequesterName, RequesterEmail = inquiry.RequesterEmail, ProposedMonthlyPrice = inquiry.ProposedMonthlyPrice, CommitmentMonths = inquiry.CommitmentMonths, LastMessagePreview = latest?.Body ?? string.Empty, LastMessageAt = inquiry.LastMessageAt, HasUnreadMessages = unread };
        }

        private static SubscriptionInquiryThreadDto ToThread(SubscriptionInquiry inquiry, bool isAdmin, string? currentUserId) => new()
        {
            InquiryId = inquiry.Id,
            PublicAccessToken = inquiry.PublicAccessToken,
            PlanName = inquiry.PlanName,
            RequesterName = inquiry.RequesterName,
            RequesterEmail = inquiry.RequesterEmail,
            PropertyCount = inquiry.PropertyCount,
            ApartmentCount = inquiry.ApartmentCount,
            TenantCount = inquiry.TenantCount,
            ProposedMonthlyPrice = inquiry.ProposedMonthlyPrice,
            CommitmentMonths = inquiry.CommitmentMonths,
            CreatedAt = inquiry.CreatedAt,
            LastMessageAt = inquiry.LastMessageAt,
            Messages = inquiry.Messages.OrderBy(m => m.CreatedAt).Select(m => new SubscriptionInquiryMessageDto { MessageId = m.Id, SenderName = m.SenderName, Body = m.Body, SentAt = m.CreatedAt, SentByCurrentUser = isAdmin ? m.SentByAdmin : !m.SentByAdmin }).ToList()
        };
    }
}
