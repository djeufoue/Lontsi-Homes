using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Common.CommunicationModels;
using Common.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Helpers;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Email;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/subscription-inquiries")]
    public class SubscriptionInquiriesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;

        public SubscriptionInquiriesController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, IEmailService emailService, IConfiguration configuration)
        {
            _context = context;
            _userManager = userManager;
            _emailService = emailService;
            _configuration = configuration;
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

            inquiry.Messages.Add(new SubscriptionInquiryMessage
            {
                SenderUserId = requester?.Id,
                SenderName = name,
                SenderEmail = email,
                SentByAdmin = false,
                Body = request.Message.Trim(),
                CreatedAt = now
            });

            _context.SubscriptionInquiries.Add(inquiry);
            await _context.SaveChangesAsync();
            await NotifyAdminsAsync(inquiry, request.Message.Trim());

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
            inquiry.Messages.Add(new SubscriptionInquiryMessage
            {
                SenderUserId = senderId,
                SenderName = senderName,
                SenderEmail = senderEmail,
                SentByAdmin = isAdmin,
                Body = body,
                CreatedAt = now
            });
            inquiry.LastMessageAt = now;
            if (isAdmin) inquiry.AdminLastReadAt = now; else inquiry.RequesterLastReadAt = now;
            await _context.SaveChangesAsync();

            if (isAdmin) await NotifyRequesterAsync(inquiry, body);
            else await NotifyAdminsAsync(inquiry, body);
        }

        private async Task NotifyAdminsAsync(SubscriptionInquiry inquiry, string message)
        {
            var admins = await _userManager.GetUsersInRoleAsync("Admin");
            var url = PortalUrl($"/Conversations?kind=subscription&conversationId={inquiry.Id}");
            foreach (var admin in admins.Where(a => !string.IsNullOrWhiteSpace(a.Email)))
            {
                var isFrench = admin.EmailLanguage == PlatformLanguage.French;
                await _emailService.SendEmailAsync(
                    admin.Email!,
                    isFrench ? $"Nouvelle demande pour le forfait {inquiry.PlanName}" : $"New {inquiry.PlanName} plan inquiry",
                    isFrench
                        ? $"{inquiry.RequesterName} ({inquiry.RequesterEmail}) demande le forfait {inquiry.PlanName}.\n\nPropriétés : {inquiry.PropertyCount}\nAppartements : {inquiry.ApartmentCount}\nLocataires : {inquiry.TenantCount}\nDurée : {inquiry.CommitmentMonths} mois\nPrix mensuel proposé : {inquiry.ProposedMonthlyPrice:N2}\nTotal proposé : {inquiry.ProposedMonthlyPrice * inquiry.CommitmentMonths:N2}\n\nMessage : {message}\n\nOuvrir la conversation : {url}"
                        : $"{inquiry.RequesterName} ({inquiry.RequesterEmail}) requested a {inquiry.PlanName} plan.\n\nProperties: {inquiry.PropertyCount}\nApartments: {inquiry.ApartmentCount}\nTenants: {inquiry.TenantCount}\nDuration: {inquiry.CommitmentMonths} months\nProposed monthly price: {inquiry.ProposedMonthlyPrice:N2}\nProposed total: {inquiry.ProposedMonthlyPrice * inquiry.CommitmentMonths:N2}\n\nMessage: {message}\n\nOpen conversation: {url}");
            }
        }

        private async Task NotifyRequesterAsync(SubscriptionInquiry inquiry, string message)
        {
            var path = inquiry.RequesterUserId == null
                ? $"/PlanInquiries/Thread?token={Uri.EscapeDataString(inquiry.PublicAccessToken)}"
                : $"/Conversations?kind=subscription&conversationId={inquiry.Id}";
            var requester = string.IsNullOrWhiteSpace(inquiry.RequesterUserId)
                ? null
                : await _userManager.FindByIdAsync(inquiry.RequesterUserId);
            var isFrench = requester?.EmailLanguage == PlatformLanguage.French;
            await _emailService.SendEmailAsync(
                inquiry.RequesterEmail,
                isFrench ? $"Réponse à votre demande pour le forfait {inquiry.PlanName}" : $"Reply to your {inquiry.PlanName} plan inquiry",
                isFrench
                    ? $"Un administrateur a répondu à votre demande de forfait.\n\nMessage : {message}\n\nOuvrir la conversation : {PortalUrl(path)}"
                    : $"An administrator replied to your plan request.\n\nMessage: {message}\n\nOpen conversation: {PortalUrl(path)}");
        }

        private string PortalUrl(string path)
        {
            var baseUrl = _configuration["Portal:BaseUrl"]?.Trim().TrimEnd('/');
            return string.IsNullOrWhiteSpace(baseUrl) ? path : baseUrl + path;
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
