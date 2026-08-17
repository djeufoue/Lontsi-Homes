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
using RentHub.API.Services.Storage;
using RentHub.API.Services.Permissions;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ConversationsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IEmailService _emailService;
        private readonly IStorageService _storageService;
        private readonly IConfiguration _configuration;
        private readonly IManagerPermissionService _permissionService;

        public ConversationsController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IEmailService emailService,
            IStorageService storageService,
            IConfiguration configuration,
            IManagerPermissionService permissionService)
        {
            _context = context;
            _userManager = userManager;
            _emailService = emailService;
            _storageService = storageService;
            _configuration = configuration;
            _permissionService = permissionService;
        }

        [HttpGet("mine")]
        public async Task<IActionResult> GetMyConversations()
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(userId))
                {
                    return Unauthorized();
                }

                var user = await _userManager.FindByIdAsync(userId);
                if (user == null)
                {
                    return Unauthorized();
                }

                var roles = await _userManager.GetRolesAsync(user);
                var isVisitor = roles.Any(r => string.Equals(r, "Visitor", StringComparison.OrdinalIgnoreCase));
                var isLandlord = roles.Any(r => string.Equals(r, "Landlord", StringComparison.OrdinalIgnoreCase));
                var isManager = roles.Any(r => string.Equals(r, "Manager", StringComparison.OrdinalIgnoreCase));
                if (!isVisitor && !isLandlord && !isManager)
                {
                    return Forbid();
                }

                var query = _context.ApartmentConversations
                    .Include(c => c.Apartment)
                    .ThenInclude(a => a!.Property)
                    .Include(c => c.Visitor)
                    .Include(c => c.Landlord)
                    .AsQueryable();

                if (isVisitor)
                {
                    query = query.Where(c => c.VisitorId == userId);
                }
                else if (isLandlord)
                {
                    query = query.Where(c => c.LandlordId == userId);
                }

                var conversations = await query
                    .OrderByDescending(c => c.LastMessageAt)
                    .ToListAsync();

                if (isManager && !isLandlord)
                {
                    var visible = new List<ApartmentConversation>();
                    foreach (var conversation in conversations)
                    {
                        if (await _permissionService.HasApartmentPermissionAsync(
                                userId, conversation.ApartmentId, ManagerPermission.ViewMessages, User.IsInRole("Admin")))
                        {
                            visible.Add(conversation);
                        }
                    }
                    conversations = visible;
                }

                var conversationIds = conversations.Select(c => c.Id).ToList();
                var latestMessages = await _context.ConversationMessages
                    .Where(m => conversationIds.Contains(m.ConversationId))
                    .GroupBy(m => m.ConversationId)
                    .Select(g => g.OrderByDescending(m => m.CreatedAt).First())
                    .ToListAsync();

                var messageMap = latestMessages.ToDictionary(m => m.ConversationId);
                var leadImageMap = await BuildLeadImageMapAsync(conversations.Select(c => c.Apartment!).ToList());

                var items = conversations.Select(conversation =>
                {
                    messageMap.TryGetValue(conversation.Id, out var latestMessage);
                    var hasUnreadMessages = isVisitor
                        ? conversation.LastLandlordMessageAt.HasValue &&
                          (!conversation.VisitorLastReadAt.HasValue || conversation.LastLandlordMessageAt > conversation.VisitorLastReadAt)
                        : conversation.LastVisitorMessageAt.HasValue &&
                          (!conversation.LandlordLastReadAt.HasValue || conversation.LastVisitorMessageAt > conversation.LandlordLastReadAt);

                    return new ConversationListItemDto
                    {
                        ConversationId = conversation.Id,
                        ApartmentId = conversation.ApartmentId,
                        ApartmentName = conversation.Apartment?.Name ?? string.Empty,
                        PropertyName = conversation.Apartment?.Property?.Name ?? string.Empty,
                        City = conversation.Apartment?.Property?.City ?? string.Empty,
                        Status = conversation.Apartment?.Status.ToString() ?? string.Empty,
                        CounterpartyName = isVisitor
                            ? (conversation.Landlord?.FullName ?? conversation.Landlord?.Email ?? "Landlord")
                            : (conversation.Visitor?.FullName ?? conversation.Visitor?.Email ?? "Visitor"),
                        LastMessagePreview = latestMessage?.Body ?? string.Empty,
                        LastMessageAt = conversation.LastMessageAt,
                        HasUnreadMessages = hasUnreadMessages,
                        LeadImageUrl = leadImageMap.TryGetValue(conversation.ApartmentId, out var leadImage) ? leadImage : null
                    };
                }).ToList();

                return Ok(items);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpGet("{conversationId:int}")]
        public async Task<IActionResult> GetConversation(int conversationId)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(userId))
                {
                    return Unauthorized();
                }

                var conversation = await LoadConversationAsync(conversationId);
                if (conversation == null)
                {
                    return NotFound("Conversation not found.");
                }

                var access = await ResolveConversationAccessAsync(conversation, userId, ManagerPermission.ViewMessages);
                if (!access.CanAccess)
                {
                    return Forbid();
                }

                if (access.IsVisitor)
                {
                    conversation.VisitorLastReadAt = DateTimeOffset.UtcNow;
                }

                if (access.IsLandlord)
                {
                    conversation.LandlordLastReadAt = DateTimeOffset.UtcNow;
                }

                await _context.SaveChangesAsync();

                return Ok(await BuildThreadDtoAsync(conversation, userId, access.IsVisitor));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpGet("apartment/{apartmentId:int}/mine")]
        [Authorize(Roles = "Visitor")]
        public async Task<IActionResult> GetMyConversationForApartment(int apartmentId)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(userId))
                {
                    return Unauthorized();
                }

                var conversation = await _context.ApartmentConversations
                    .FirstOrDefaultAsync(c => c.ApartmentId == apartmentId && c.VisitorId == userId);

                if (conversation == null)
                {
                    return NotFound();
                }

                return await GetConversation(conversation.Id);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPost("apartment/{apartmentId:int}/start")]
        [Authorize(Roles = "Visitor")]
        public async Task<IActionResult> StartConversation(int apartmentId, [FromBody] StartConversationRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(userId))
                {
                    return Unauthorized();
                }

                var visitor = await _userManager.FindByIdAsync(userId);
                if (visitor == null)
                {
                    return Unauthorized();
                }

                var apartment = await _context.Apartments
                    .Include(a => a.Property)
                    .ThenInclude(p => p!.Landlord)
                    .FirstOrDefaultAsync(a => a.Id == apartmentId && !a.IsDeleted);

                if (apartment == null || apartment.Property == null)
                {
                    return NotFound("Apartment not found.");
                }

                var conversation = await _context.ApartmentConversations
                    .FirstOrDefaultAsync(c => c.ApartmentId == apartmentId && c.VisitorId == userId);

                if (conversation == null)
                {
                    conversation = new ApartmentConversation
                    {
                        ApartmentId = apartmentId,
                        LandlordId = apartment.Property.LandlordId,
                        VisitorId = userId,
                        CreatedAt = DateTimeOffset.UtcNow,
                        LastMessageAt = DateTimeOffset.UtcNow,
                        VisitorLastReadAt = DateTimeOffset.UtcNow
                    };

                    _context.ApartmentConversations.Add(conversation);
                    await _context.SaveChangesAsync();
                }

                await AddMessageAsync(conversation, visitor, request.InitialMessage.Trim(), notifyLandlord: true);
                conversation = await LoadConversationAsync(conversation.Id) ?? conversation;

                return Ok(await BuildThreadDtoAsync(conversation, userId, isVisitor: true));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPost("{conversationId:int}/messages")]
        public async Task<IActionResult> SendMessage(int conversationId, [FromBody] CreateConversationMessageRequest request)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ModelState);
                }

                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(userId))
                {
                    return Unauthorized();
                }

                var user = await _userManager.FindByIdAsync(userId);
                if (user == null)
                {
                    return Unauthorized();
                }

                var conversation = await LoadConversationAsync(conversationId);
                if (conversation == null)
                {
                    return NotFound("Conversation not found.");
                }

                var access = await ResolveConversationAccessAsync(conversation, userId, ManagerPermission.SendMessages);
                if (!access.CanAccess)
                {
                    return Forbid();
                }

                await AddMessageAsync(conversation, user, request.Message.Trim(), notifyLandlord: access.IsVisitor);
                conversation = await LoadConversationAsync(conversationId) ?? conversation;

                return Ok(await BuildThreadDtoAsync(conversation, userId, access.IsVisitor));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        private async Task AddMessageAsync(ApartmentConversation conversation, ApplicationUser sender, string body, bool notifyLandlord)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                throw new InvalidOperationException("Message cannot be empty.");
            }

            var now = DateTimeOffset.UtcNow;
            _context.ConversationMessages.Add(new ConversationMessage
            {
                ConversationId = conversation.Id,
                SenderId = sender.Id,
                Body = body,
                CreatedAt = now
            });

            conversation.LastMessageAt = now;
            if (string.Equals(sender.Id, conversation.VisitorId, StringComparison.Ordinal))
            {
                conversation.LastVisitorMessageAt = now;
                conversation.VisitorLastReadAt = now;
            }
            else
            {
                conversation.LastLandlordMessageAt = now;
                conversation.LandlordLastReadAt = now;
            }

            await _context.SaveChangesAsync();

            if (notifyLandlord)
            {
                await NotifyLandlordAsync(conversation, sender, body);
            }
        }

        private async Task NotifyLandlordAsync(ApartmentConversation conversation, ApplicationUser sender, string body)
        {
            var landlord = await _userManager.FindByIdAsync(conversation.LandlordId);
            if (landlord == null || string.IsNullOrWhiteSpace(landlord.Email))
            {
                return;
            }

            var apartment = await _context.Apartments
                .Include(a => a.Property)
                .FirstOrDefaultAsync(a => a.Id == conversation.ApartmentId);

            var portalBaseUrl = _configuration["Portal:BaseUrl"]?.Trim().TrimEnd('/');
            var inboxUrl = string.IsNullOrWhiteSpace(portalBaseUrl)
                ? string.Empty
                : $"{portalBaseUrl}/Conversations?conversationId={conversation.Id}";

            var visitorLabel = string.IsNullOrWhiteSpace(sender.FullName) ? (sender.Email ?? "A visitor") : sender.FullName;
            var isFrench = landlord.EmailLanguage == PlatformLanguage.French;
            var lines = new List<string>
            {
                isFrench ? $"Bonjour {(landlord.FullName ?? landlord.Email ?? "Bailleur")}," : $"Hello {(landlord.FullName ?? landlord.Email ?? "Landlord")},",
                string.Empty,
                isFrench
                    ? $"{visitorLabel} vous a envoyé un nouveau message privé au sujet de {(apartment?.Name ?? "votre appartement")}."
                    : $"{visitorLabel} sent you a new private message about {(apartment?.Name ?? "your apartment")}.",
                string.Empty,
                isFrench ? $"Aperçu du message : {body}" : $"Message preview: {body}",
                string.Empty
            };

            if (!string.IsNullOrWhiteSpace(inboxUrl))
            {
                lines.Add(isFrench ? $"Ouvrir la conversation : {inboxUrl}" : $"Open the conversation: {inboxUrl}");
                lines.Add(string.Empty);
            }

            lines.Add(isFrench ? "Ce message a été envoyé depuis le site public Lontsi Homes." : "This message was sent from the RentHub public listing experience.");

            await _emailService.SendEmailAsync(
                landlord.Email!,
                isFrench ? $"Demande Lontsi Homes pour {(apartment?.Name ?? "votre appartement")}" : $"RentHub inquiry for {(apartment?.Name ?? "your apartment")}",
                string.Join(Environment.NewLine, lines));
        }

        private async Task<ApartmentConversation?> LoadConversationAsync(int conversationId)
        {
            return await _context.ApartmentConversations
                .Include(c => c.Apartment)
                .ThenInclude(a => a!.Property)
                .Include(c => c.Visitor)
                .Include(c => c.Landlord)
                .Include(c => c.Messages)
                .ThenInclude(m => m.Sender)
                .FirstOrDefaultAsync(c => c.Id == conversationId);
        }

        private async Task<(bool CanAccess, bool IsVisitor, bool IsLandlord)> ResolveConversationAccessAsync(
            ApartmentConversation conversation,
            string userId,
            ManagerPermission managerPermission)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return (false, false, false);
            }

            var isVisitor = string.Equals(conversation.VisitorId, userId, StringComparison.Ordinal);
            var isLandlord = string.Equals(conversation.LandlordId, userId, StringComparison.Ordinal);
            var isAuthorizedManager = !isVisitor && !isLandlord &&
                await _permissionService.HasApartmentPermissionAsync(
                    userId, conversation.ApartmentId, managerPermission, User.IsInRole("Admin"));
            return (isVisitor || isLandlord || isAuthorizedManager, isVisitor, isLandlord || isAuthorizedManager);
        }

        private async Task<ConversationThreadDto> BuildThreadDtoAsync(ApartmentConversation conversation, string currentUserId, bool isVisitor)
        {
            var leadImageMap = await BuildLeadImageMapAsync(new List<Apartment> { conversation.Apartment! });

            return new ConversationThreadDto
            {
                ConversationId = conversation.Id,
                ApartmentId = conversation.ApartmentId,
                ApartmentName = conversation.Apartment?.Name ?? string.Empty,
                PropertyName = conversation.Apartment?.Property?.Name ?? string.Empty,
                City = conversation.Apartment?.Property?.City ?? string.Empty,
                Address = conversation.Apartment?.Property?.Address ?? string.Empty,
                Status = conversation.Apartment?.Status.ToString() ?? string.Empty,
                Price = conversation.Apartment?.Price ?? 0,
                LandlordName = conversation.Landlord?.FullName ?? conversation.Landlord?.Email ?? "Landlord",
                VisitorName = conversation.Visitor?.FullName ?? conversation.Visitor?.Email ?? "Visitor",
                CounterpartyName = isVisitor
                    ? (conversation.Landlord?.FullName ?? conversation.Landlord?.Email ?? "Landlord")
                    : (conversation.Visitor?.FullName ?? conversation.Visitor?.Email ?? "Visitor"),
                LeadImageUrl = leadImageMap.TryGetValue(conversation.ApartmentId, out var leadImage) ? leadImage : null,
                CreatedAt = conversation.CreatedAt,
                LastMessageAt = conversation.LastMessageAt,
                Messages = conversation.Messages
                    .OrderBy(m => m.CreatedAt)
                    .Select(message => new ConversationMessageDto
                    {
                        MessageId = message.Id,
                        SenderId = message.SenderId,
                        SenderName = message.Sender?.FullName ?? message.Sender?.Email ?? "User",
                        Body = message.Body,
                        SentAt = message.CreatedAt,
                        SentByCurrentUser = string.Equals(message.SenderId, currentUserId, StringComparison.Ordinal),
                        IsRead = isVisitor
                            ? !conversation.LastLandlordMessageAt.HasValue || conversation.VisitorLastReadAt >= message.CreatedAt
                            : !conversation.LastVisitorMessageAt.HasValue || conversation.LandlordLastReadAt >= message.CreatedAt
                    })
                    .ToList()
            };
        }

        private async Task<Dictionary<int, string>> BuildLeadImageMapAsync(IReadOnlyCollection<Apartment> apartments)
        {
            var apartmentIds = apartments.Select(a => a.Id).Distinct().ToList();
            var propertyIds = apartments.Select(a => a.PropertyId).Distinct().ToList();

            var apartmentImages = await _context.Documents
                .Where(d => d.ApartmentId != null &&
                            apartmentIds.Contains(d.ApartmentId.Value) &&
                            d.DocumentType == DocumentTypeEnum.ApartmentImage)
                .GroupBy(d => d.ApartmentId!.Value)
                .Select(g => g.OrderByDescending(d => d.CreatedAt).First())
                .ToListAsync();

            var propertyImages = await _context.Documents
                .Where(d => d.PropertyId != null &&
                            propertyIds.Contains(d.PropertyId.Value) &&
                            d.ApartmentId == null &&
                            d.DocumentType == DocumentTypeEnum.PropertyImage)
                .GroupBy(d => d.PropertyId!.Value)
                .Select(g => g.OrderByDescending(d => d.CreatedAt).First())
                .ToListAsync();

            var apartmentImageUrls = new Dictionary<int, string>();
            foreach (var image in apartmentImages)
            {
                if (image.ApartmentId.HasValue)
                {
                    apartmentImageUrls[image.ApartmentId.Value] = await _storageService.GetReadUrlAsync(image.BlobUrl);
                }
            }

            var propertyImageUrls = new Dictionary<int, string>();
            foreach (var image in propertyImages)
            {
                if (image.PropertyId.HasValue)
                {
                    propertyImageUrls[image.PropertyId.Value] = await _storageService.GetReadUrlAsync(image.BlobUrl);
                }
            }

            var result = new Dictionary<int, string>();
            foreach (var apartment in apartments)
            {
                if (apartmentImageUrls.TryGetValue(apartment.Id, out var apartmentUrl))
                {
                    result[apartment.Id] = apartmentUrl;
                }
                else if (propertyImageUrls.TryGetValue(apartment.PropertyId, out var propertyUrl))
                {
                    result[apartment.Id] = propertyUrl;
                }
            }

            return result;
        }
    }
}
