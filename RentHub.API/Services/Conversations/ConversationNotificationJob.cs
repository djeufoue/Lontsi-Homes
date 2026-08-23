using System.Net;
using Common.Enums;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Email;
using RentHub.API.Services.Permissions;

namespace RentHub.API.Services.Conversations
{
    public class ConversationNotificationJob : IConversationNotificationJob
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IEmailService _emailService;
        private readonly IManagerPermissionService _permissionService;
        private readonly IConfiguration _configuration;

        public ConversationNotificationJob(ApplicationDbContext context, UserManager<ApplicationUser> userManager,
            IEmailService emailService, IManagerPermissionService permissionService, IConfiguration configuration)
        {
            _context = context;
            _userManager = userManager;
            _emailService = emailService;
            _permissionService = permissionService;
            _configuration = configuration;
        }

        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 120, 600 })]
        public async Task SendMessageNotificationsAsync(int messageId)
        {
            var message = await _context.ConversationMessages
                .Include(item => item.Sender)
                .Include(item => item.Conversation).ThenInclude(c => c!.Apartment).ThenInclude(a => a!.Property)
                .FirstOrDefaultAsync(item => item.Id == messageId);
            var conversation = message?.Conversation;
            var apartment = conversation?.Apartment;
            var property = apartment?.Property;
            var sender = message?.Sender;
            if (message == null || conversation == null || apartment == null || property == null || sender == null) return;

            var recipientIds = conversation.IsPropertyTeamConversation
                ? await ResolveTeamRecipientsAsync(property, apartment, sender.Id)
                : await ResolveResidentRecipientsAsync(conversation, property, apartment, sender.Id);
            var recipients = await _context.Users.Where(u =>
                recipientIds.Contains(u.Id) &&
                u.Email != null &&
                u.ConversationEmailNotificationsEnabled).ToListAsync();
            foreach (var recipient in recipients)
            {
                await SendEmailAsync(recipient, sender, property, apartment.Name, conversation, message);
            }
        }

        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 120, 600 })]
        public async Task SendSubscriptionInquiryNotificationsAsync(int messageId)
        {
            var message = await _context.SubscriptionInquiryMessages
                .Include(item => item.SubscriptionInquiry)
                .FirstOrDefaultAsync(item => item.Id == messageId);
            var inquiry = message?.SubscriptionInquiry;
            if (message == null || inquiry == null) return;

            if (message.SentByAdmin)
            {
                var requester = string.IsNullOrWhiteSpace(inquiry.RequesterUserId)
                    ? null
                    : await _userManager.FindByIdAsync(inquiry.RequesterUserId);
                var fr = requester?.EmailLanguage == PlatformLanguage.French;
                var path = inquiry.RequesterUserId == null
                    ? $"/PlanInquiries/Thread?token={Uri.EscapeDataString(inquiry.PublicAccessToken)}"
                    : $"/Conversations?kind=subscription&conversationId={inquiry.Id}";
                if (requester == null || requester.ConversationEmailNotificationsEnabled)
                {
                    await SendSimpleEmailAsync(
                        inquiry.RequesterEmail,
                        fr ? $"Réponse à votre demande pour le forfait {inquiry.PlanName}" : $"Reply to your {inquiry.PlanName} plan inquiry",
                        fr
                            ? $"Un administrateur a répondu à votre demande de forfait.\n\nMessage : {message.Body}\n\nOuvrir la conversation : {PortalUrl(path)}"
                            : $"An administrator replied to your plan request.\n\nMessage: {message.Body}\n\nOpen conversation: {PortalUrl(path)}");
                }
                return;
            }

            var admins = await _userManager.GetUsersInRoleAsync("Admin");
            var url = PortalUrl($"/Conversations?kind=subscription&conversationId={inquiry.Id}");
            foreach (var admin in admins.Where(admin =>
                         !string.IsNullOrWhiteSpace(admin.Email) &&
                         admin.ConversationEmailNotificationsEnabled))
            {
                var fr = admin.EmailLanguage == PlatformLanguage.French;
                await SendSimpleEmailAsync(
                    admin.Email!,
                    fr ? $"Nouvelle demande pour le forfait {inquiry.PlanName}" : $"New {inquiry.PlanName} plan inquiry",
                    fr
                        ? $"{inquiry.RequesterName} ({inquiry.RequesterEmail}) vous a envoyé un message.\n\nMessage : {message.Body}\n\nOuvrir la conversation : {url}"
                        : $"{inquiry.RequesterName} ({inquiry.RequesterEmail}) sent you a message.\n\nMessage: {message.Body}\n\nOpen conversation: {url}");
            }
        }

        private async Task<HashSet<string>> ResolveResidentRecipientsAsync(ApartmentConversation conversation,
            Property property, Apartment apartment, string senderId)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (string.Equals(senderId, conversation.VisitorId, StringComparison.Ordinal))
            {
                result.Add(property.LandlordId);
                var managerIds = await _context.PropertyManagerAssignments
                    .Where(a => a.PropertyId == property.Id && a.ManagerId != senderId).Select(a => a.ManagerId).ToListAsync();
                foreach (var managerId in managerIds)
                    if (await _permissionService.HasApartmentPermissionAsync(managerId, apartment.Id, ManagerPermission.ViewMessages, false))
                        result.Add(managerId);
            }
            else result.Add(conversation.VisitorId);
            result.Remove(senderId);
            return result;
        }

        private async Task<HashSet<string>> ResolveTeamRecipientsAsync(Property property, Apartment apartment, string senderId)
        {
            var result = new HashSet<string>(StringComparer.Ordinal) { property.LandlordId };
            var managerIds = await _context.PropertyManagerAssignments
                .Where(a => a.PropertyId == property.Id && a.ManagerId != senderId).Select(a => a.ManagerId).ToListAsync();
            foreach (var managerId in managerIds)
                if (await _permissionService.HasPropertyPermissionAsync(managerId, property.Id, ManagerPermission.ViewMessages, false))
                    result.Add(managerId);
            foreach (var admin in await _userManager.GetUsersInRoleAsync("Admin")) result.Add(admin.Id);
            result.Remove(senderId);
            return result;
        }

        private async Task SendEmailAsync(ApplicationUser recipient, ApplicationUser sender, Property property,
            string apartmentName, ApartmentConversation conversation, ConversationMessage message)
        {
            if (string.IsNullOrWhiteSpace(recipient.Email)) return;
            var fr = recipient.EmailLanguage == PlatformLanguage.French;
            var baseUrl = (_configuration["Portal:BaseUrl"] ?? "https://localhost:7059").Trim().TrimEnd('/');
            var kind = conversation.IsPropertyTeamConversation ? "team" : "apartment";
            var url = $"{baseUrl}/Conversations?kind={kind}&conversationId={conversation.Id}";
            var residentSender = !conversation.IsPropertyTeamConversation && sender.Id == conversation.VisitorId;
            var senderName = residentSender
                ? sender.FullName ?? sender.Email ?? (fr ? "Un locataire" : "A tenant")
                : conversation.IsPropertyTeamConversation
                    ? sender.FullName ?? sender.Email ?? (fr ? "Un membre de l'équipe" : "A team member")
                    : fr ? $"Équipe de {property.Name}" : $"{property.Name} team";
            var context = conversation.IsPropertyTeamConversation
                ? (fr ? $"équipe interne · {property.Name}" : $"internal team · {property.Name}")
                : $"{property.Name} · {apartmentName}";
            var subject = fr ? $"Nouveau message · {context}" : $"New message · {context}";
            var plain = fr
                ? $"Bonjour {recipient.FullName ?? recipient.Email},\n\n{senderName} vous a envoyé un message concernant {context}.\n\n{message.Body}\n\nConsulter le message dans Lontsi Homes : {url}\n\nLes réponses sont possibles uniquement dans la plateforme."
                : $"Hello {recipient.FullName ?? recipient.Email},\n\n{senderName} sent you a message about {context}.\n\n{message.Body}\n\nView the message in Lontsi Homes: {url}\n\nReplies are available only inside the platform.";
            var body = WebUtility.HtmlEncode(message.Body).Replace("\n", "<br />", StringComparison.Ordinal);
            var html = $"<p>{(fr ? "Bonjour" : "Hello")} {WebUtility.HtmlEncode(recipient.FullName ?? recipient.Email)},</p><p><strong>{WebUtility.HtmlEncode(senderName)}</strong> {(fr ? "vous a envoyé un message concernant" : "sent you a message about")} <strong>{WebUtility.HtmlEncode(context)}</strong>.</p><div style=\"margin:20px 0;padding:16px;border-left:4px solid #a7b83f;background:#f4f5ec;border-radius:8px;\">{body}</div><p><a href=\"{WebUtility.HtmlEncode(url)}\" style=\"display:inline-block;padding:12px 18px;background:#cbd968;color:#20241f;text-decoration:none;border-radius:10px;font-weight:700;\">{(fr ? "Consulter le message" : "View message")}</a></p>";
            await _emailService.TrySendEmailAsync(new EmailMessage { To = recipient.Email, Subject = subject, PlainTextBody = plain, HtmlBody = html });
        }

        private string PortalUrl(string path)
        {
            var baseUrl = (_configuration["Portal:BaseUrl"] ?? string.Empty).Trim().TrimEnd('/');
            return string.IsNullOrWhiteSpace(baseUrl) ? path : baseUrl + path;
        }

        private Task SendSimpleEmailAsync(string to, string subject, string plainText) =>
            _emailService.TrySendEmailAsync(new EmailMessage
            {
                To = to,
                Subject = subject,
                PlainTextBody = plainText,
                HtmlBody = $"<div style=\"font-family:Arial,sans-serif;line-height:1.6\">{WebUtility.HtmlEncode(plainText).Replace("\n", "<br />", StringComparison.Ordinal)}</div>"
            });
    }
}
