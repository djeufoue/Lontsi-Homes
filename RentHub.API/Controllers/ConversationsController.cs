using Common.CommunicationModels;
using Common.Enums;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Helpers;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Conversations;
using RentHub.API.Services.Permissions;
using RentHub.API.Services.Storage;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ConversationsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IStorageService _storageService;
        private readonly IManagerPermissionService _permissionService;
        private readonly IBackgroundJobClient _backgroundJobs;

        public ConversationsController(
            ApplicationDbContext context,
            UserManager<ApplicationUser> userManager,
            IStorageService storageService,
            IManagerPermissionService permissionService,
            IBackgroundJobClient backgroundJobs)
        {
            _context = context;
            _userManager = userManager;
            _storageService = storageService;
            _permissionService = permissionService;
            _backgroundJobs = backgroundJobs;
        }

        [HttpGet("mine")]
        public async Task<IActionResult> GetMyConversations([FromQuery] int? propertyId = null, [FromQuery] int? apartmentId = null)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized();
            }

            var conversations = await LoadVisibleConversationsAsync(userId);
            if (propertyId.HasValue)
            {
                conversations = conversations.Where(c => c.Apartment?.PropertyId == propertyId.Value).ToList();
            }

            if (apartmentId.HasValue)
            {
                conversations = conversations.Where(c => c.ApartmentId == apartmentId.Value).ToList();
            }

            var conversationIds = conversations.Select(c => c.Id).ToList();
            var messages = conversationIds.Count == 0
                ? new List<ConversationMessage>()
                : await _context.ConversationMessages
                    .Where(m => conversationIds.Contains(m.ConversationId))
                    .OrderByDescending(m => m.CreatedAt)
                    .ToListAsync();
            var readStates = conversationIds.Count == 0
                ? new List<ConversationReadState>()
                : await _context.ConversationReadStates
                    .Where(s => conversationIds.Contains(s.ConversationId) && s.UserId == userId)
                    .ToListAsync();

            var messagesByConversation = messages.GroupBy(m => m.ConversationId)
                .ToDictionary(g => g.Key, g => g.ToList());
            var readStateMap = readStates.ToDictionary(s => s.ConversationId, s => s.LastReadAt);
            var leadImageMap = await BuildLeadImageMapAsync(conversations
                .Where(c => c.Apartment != null)
                .Select(c => c.Apartment!)
                .ToList());

            var items = conversations.Select(conversation =>
            {
                var isResident = !conversation.IsPropertyTeamConversation &&
                                 string.Equals(conversation.VisitorId, userId, StringComparison.Ordinal);
                messagesByConversation.TryGetValue(conversation.Id, out var threadMessages);
                threadMessages ??= new List<ConversationMessage>();
                var lastMessage = threadMessages.FirstOrDefault();
                var lastReadAt = ResolveLastReadAt(conversation, userId, isResident, readStateMap);
                var unreadCount = threadMessages.Count(message =>
                    !string.Equals(message.SenderId, userId, StringComparison.Ordinal) &&
                    (!lastReadAt.HasValue || message.CreatedAt > lastReadAt.Value));

                return new ConversationListItemDto
                {
                    ConversationId = conversation.Id,
                    ApartmentId = conversation.ApartmentId,
                    PropertyId = conversation.Apartment?.PropertyId ?? 0,
                    ApartmentName = conversation.IsPropertyTeamConversation ? string.Empty : conversation.Apartment?.Name ?? string.Empty,
                    PropertyName = conversation.Apartment?.Property?.Name ?? string.Empty,
                    City = conversation.Apartment?.Property?.City ?? string.Empty,
                    Status = conversation.Apartment?.Status.ToString() ?? string.Empty,
                    CounterpartyName = conversation.IsPropertyTeamConversation
                        ? "Internal property team"
                        : isResident
                        ? $"{conversation.Apartment?.Property?.Name ?? "Property"} team"
                        : (conversation.Visitor?.FullName ?? conversation.Visitor?.Email ?? "Tenant"),
                    LastMessagePreview = lastMessage?.Body ?? string.Empty,
                    LastMessageAt = conversation.LastMessageAt,
                    HasUnreadMessages = unreadCount > 0,
                    UnreadCount = unreadCount,
                    LeadImageUrl = leadImageMap.TryGetValue(conversation.ApartmentId, out var leadImage) ? leadImage : null,
                    IsPropertyTeamConversation = conversation.IsPropertyTeamConversation
                };
            }).OrderByDescending(item => item.LastMessageAt).ToList();

            return Ok(items);
        }

        [HttpGet("workspace")]
        public async Task<IActionResult> GetWorkspace()
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
            var isTenant = roles.Any(role => string.Equals(role, "Tenant", StringComparison.OrdinalIgnoreCase));
            var isLandlord = roles.Any(role => string.Equals(role, "Landlord", StringComparison.OrdinalIgnoreCase));
            var isManager = roles.Any(role => string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase));
            var isAdmin = roles.Any(role => string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase));
            var now = DateTimeOffset.UtcNow;

            var residentApartmentIds = isTenant
                ? await _context.Tenancies
                    .Where(tenancy =>
                        tenancy.StartDate <= now &&
                        (!tenancy.EndDate.HasValue || tenancy.EndDate.Value >= now) &&
                        (!tenancy.TerminatedAt.HasValue || tenancy.TerminatedAt.Value > now) &&
                        tenancy.Members.Any(member => member.MemberId == userId))
                    .Select(tenancy => tenancy.ApartmentId)
                    .Distinct()
                    .ToListAsync()
                : new List<int>();
            var ownedPropertyIds = isLandlord
                ? await _context.Properties.Where(p => p.LandlordId == userId).Select(p => p.Id).ToListAsync()
                : new List<int>();
            var managedPropertyIds = isManager
                ? await _context.PropertyManagerAssignments.Where(a => a.ManagerId == userId).Select(a => a.PropertyId).ToListAsync()
                : new List<int>();
            var adminPropertyIds = isAdmin
                ? await _context.Properties.Select(property => property.Id).ToListAsync()
                : new List<int>();

            var candidateApartments = await _context.Apartments
                .Include(apartment => apartment.Property)
                .Where(apartment =>
                    residentApartmentIds.Contains(apartment.Id) ||
                    ownedPropertyIds.Contains(apartment.PropertyId) ||
                    managedPropertyIds.Contains(apartment.PropertyId) ||
                    adminPropertyIds.Contains(apartment.PropertyId))
                .OrderBy(apartment => apartment.Property!.Name)
                .ThenBy(apartment => apartment.Name)
                .ToListAsync();

            var apartmentScopes = new List<ConversationApartmentScope>();
            foreach (var apartment in candidateApartments)
            {
                var isResidentApartment = residentApartmentIds.Contains(apartment.Id);
                var isOwned = ownedPropertyIds.Contains(apartment.PropertyId);
                var canViewAsManager = isManager && await _permissionService.HasApartmentPermissionAsync(
                    userId, apartment.Id, ManagerPermission.ViewMessages, false);
                if (!isResidentApartment && !isOwned && !canViewAsManager && !isAdmin)
                {
                    continue;
                }

                var canBroadcast = isOwned || (isManager && await _permissionService.HasApartmentPermissionAsync(
                    userId, apartment.Id, ManagerPermission.SendMessages, false));
                var canUseTeam = isOwned || isAdmin || canBroadcast;
                apartmentScopes.Add(new ConversationApartmentScope(apartment, isResidentApartment, canBroadcast, canUseTeam));
            }

            var activeTenantCounts = new Dictionary<int, int>();
            foreach (var propertyGroup in apartmentScopes.GroupBy(scope => scope.Apartment.PropertyId))
            {
                var apartmentIds = propertyGroup.Select(scope => scope.Apartment.Id).ToList();
                activeTenantCounts[propertyGroup.Key] = await _context.TenancyMembers
                    .Where(member =>
                        apartmentIds.Contains(member.Tenancy!.ApartmentId) &&
                        member.Tenancy.StartDate <= now &&
                        (!member.Tenancy.EndDate.HasValue || member.Tenancy.EndDate.Value >= now) &&
                        (!member.Tenancy.TerminatedAt.HasValue || member.Tenancy.TerminatedAt.Value > now))
                    .Select(member => member.MemberId)
                    .Distinct()
                    .CountAsync();
            }

            var teamConversationMap = await _context.ApartmentConversations
                .Where(c => c.IsPropertyTeamConversation)
                .Select(c => new { c.Id, PropertyId = c.Apartment!.PropertyId })
                .ToDictionaryAsync(c => c.PropertyId, c => (int?)c.Id);
            var properties = apartmentScopes
                .GroupBy(scope => new
                {
                    scope.Apartment.PropertyId,
                    PropertyName = scope.Apartment.Property?.Name ?? string.Empty
                })
                .Select(group => new ConversationPropertyOptionDto
                {
                    PropertyId = group.Key.PropertyId,
                    PropertyName = group.Key.PropertyName,
                    ActiveTenantCount = activeTenantCounts.GetValueOrDefault(group.Key.PropertyId),
                    CanBroadcast = group.Any(scope => scope.CanBroadcast),
                    CanUsePropertyTeamConversation = group.Any(scope => scope.CanUseTeamConversation),
                    PropertyTeamConversationId = teamConversationMap.GetValueOrDefault(group.Key.PropertyId),
                    Apartments = group.Select(scope => new ConversationApartmentOptionDto
                    {
                        ApartmentId = scope.Apartment.Id,
                        ApartmentName = scope.Apartment.Name,
                        CanStartConversation = scope.IsResidentApartment
                    }).OrderBy(apartment => apartment.ApartmentName).ToList()
                })
                .OrderBy(property => property.PropertyName)
                .ToList();

            return Ok(new ConversationWorkspaceDto
            {
                CanStartConversation = apartmentScopes.Any(scope => scope.IsResidentApartment),
                CanBroadcast = properties.Any(property => property.CanBroadcast),
                CanUsePropertyTeamConversation = properties.Any(property => property.CanUsePropertyTeamConversation),
                Properties = properties
            });
        }

        [HttpGet("unread-count")]
        public async Task<IActionResult> GetUnreadCount()
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized();
            }

            var conversations = await LoadVisibleConversationsAsync(userId);
            var conversationIds = conversations.Select(c => c.Id).ToList();
            if (conversationIds.Count == 0)
            {
                return Ok(new ConversationUnreadCountDto());
            }

            var messages = await _context.ConversationMessages
                .Where(m => conversationIds.Contains(m.ConversationId) && m.SenderId != userId)
                .Select(m => new { m.ConversationId, m.CreatedAt })
                .ToListAsync();
            var readStates = await _context.ConversationReadStates
                .Where(s => conversationIds.Contains(s.ConversationId) && s.UserId == userId)
                .ToDictionaryAsync(s => s.ConversationId, s => s.LastReadAt);
            var conversationMap = conversations.ToDictionary(c => c.Id);
            var unreadByConversation = messages
                .GroupBy(m => m.ConversationId)
                .Select(group =>
                {
                    var conversation = conversationMap[group.Key];
                    var isResident = !conversation.IsPropertyTeamConversation && string.Equals(conversation.VisitorId, userId, StringComparison.Ordinal);
                    var readAt = ResolveLastReadAt(conversation, userId, isResident, readStates);
                    return group.Count(message => !readAt.HasValue || message.CreatedAt > readAt.Value);
                })
                .Where(count => count > 0)
                .ToList();

            return Ok(new ConversationUnreadCountDto
            {
                UnreadCount = unreadByConversation.Sum(),
                UnreadConversationCount = unreadByConversation.Count
            });
        }

        [HttpGet("{conversationId:int}")]
        public async Task<IActionResult> GetConversation(int conversationId)
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

            var viewAccess = await ResolveConversationAccessAsync(conversation, userId, ManagerPermission.ViewMessages);
            if (!viewAccess.CanAccess)
            {
                return Forbid();
            }

            await MarkConversationReadAsync(conversation, userId, viewAccess.IsResident);
            var sendAccess = await ResolveConversationAccessAsync(conversation, userId, ManagerPermission.SendMessages);
            return Ok(await BuildThreadDtoAsync(conversation, userId, viewAccess.IsResident, sendAccess.CanAccess));
        }

        [HttpGet("apartment/{apartmentId:int}/mine")]
        [Authorize(Roles = "Visitor,Tenant")]
        public async Task<IActionResult> GetMyConversationForApartment(int apartmentId)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized();
            }

            var conversation = await _context.ApartmentConversations
                .FirstOrDefaultAsync(c => c.ApartmentId == apartmentId && c.VisitorId == userId && !c.IsPropertyTeamConversation);
            return conversation == null ? NotFound() : await GetConversation(conversation.Id);
        }

        [HttpPost("apartment/{apartmentId:int}/start")]
        [Authorize(Roles = "Visitor,Tenant")]
        public async Task<IActionResult> StartConversation(int apartmentId, [FromBody] StartConversationRequest request)
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

            var resident = await _userManager.FindByIdAsync(userId);
            if (resident == null)
            {
                return Unauthorized();
            }

            var roles = await _userManager.GetRolesAsync(resident);
            var isTenant = roles.Any(role => string.Equals(role, "Tenant", StringComparison.OrdinalIgnoreCase));
            if (isTenant && !await HasActiveTenancyMembershipAsync(userId, apartmentId))
            {
                return Forbid();
            }

            var apartment = await _context.Apartments
                .Include(a => a.Property)
                .ThenInclude(p => p!.Landlord)
                .FirstOrDefaultAsync(a => a.Id == apartmentId);
            if (apartment?.Property == null)
            {
                return NotFound("Apartment not found.");
            }

            var conversation = await GetOrCreateConversationAsync(apartment, userId);
            await AddMessageAsync(conversation, resident, request.InitialMessage.Trim(), false, true, null);
            conversation = await LoadConversationAsync(conversation.Id) ?? conversation;
            return Ok(await BuildThreadDtoAsync(conversation, userId, true, true));
        }

        [HttpPost("{conversationId:int}/messages")]
        public async Task<IActionResult> SendMessage(int conversationId, [FromBody] CreateConversationMessageRequest request)
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

            if (request.ReplyToMessageId.HasValue && !await IsValidReplyTargetAsync(conversationId, request.ReplyToMessageId.Value))
            {
                return BadRequest(new { Message = "The message selected for this reply is not part of this conversation." });
            }

            await AddMessageAsync(conversation, user, request.Message.Trim(), false, true, request.ReplyToMessageId);
            conversation = await LoadConversationAsync(conversationId) ?? conversation;
            return Ok(await BuildThreadDtoAsync(conversation, userId, access.IsResident, true));
        }

        [HttpPost("property/{propertyId:int}/team/start")]
        [Authorize(Roles = "Landlord,Manager,Admin")]
        public async Task<IActionResult> StartPropertyTeamConversation(int propertyId, [FromBody] StartPropertyTeamConversationRequest request)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            var userId = UserHelpers.GetUserId(User);
            var sender = string.IsNullOrWhiteSpace(userId) ? null : await _userManager.FindByIdAsync(userId);
            var property = await _context.Properties.Include(p => p.Apartments).FirstOrDefaultAsync(p => p.Id == propertyId);
            if (sender == null) return Unauthorized();
            if (property == null) return NotFound("Property not found.");
            var apartment = property.Apartments.OrderBy(a => a.Id).FirstOrDefault();
            if (apartment == null) return BadRequest(new { Message = "Add an apartment before starting the internal property conversation." });
            apartment.Property = property;
            var probe = new ApartmentConversation { ApartmentId = apartment.Id, Apartment = apartment, VisitorId = property.LandlordId, IsPropertyTeamConversation = true };
            var access = await ResolveConversationAccessAsync(probe, userId!, ManagerPermission.SendMessages);
            if (!access.CanAccess) return Forbid();
            var conversation = await GetOrCreatePropertyTeamConversationAsync(apartment);
            await AddMessageAsync(conversation, sender, request.InitialMessage.Trim(), false, true, null);
            conversation = await LoadConversationAsync(conversation.Id) ?? conversation;
            return Ok(await BuildThreadDtoAsync(conversation, userId!, false, true));
        }

        [HttpPost("property/{propertyId:int}/broadcast")]
        [Authorize(Roles = "Landlord,Manager")]
        public async Task<IActionResult> BroadcastToProperty(int propertyId, [FromBody] CreatePropertyBroadcastRequest request)
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

            var sender = await _userManager.FindByIdAsync(userId);
            var property = await _context.Properties.Include(p => p.Landlord).FirstOrDefaultAsync(p => p.Id == propertyId);
            if (sender == null || property == null)
            {
                return NotFound();
            }

            var apartmentIds = await _context.Apartments
                .Where(a => a.PropertyId == propertyId)
                .Select(a => a.Id)
                .ToListAsync();
            var authorizedApartmentIds = new List<int>();
            foreach (var apartmentId in apartmentIds)
            {
                if (await _permissionService.HasApartmentPermissionAsync(
                        userId, apartmentId, ManagerPermission.SendMessages, false))
                {
                    authorizedApartmentIds.Add(apartmentId);
                }
            }

            if (authorizedApartmentIds.Count == 0)
            {
                return Forbid();
            }

            var now = DateTimeOffset.UtcNow;
            var memberships = await _context.TenancyMembers
                .Include(member => member.Member)
                .Include(member => member.Tenancy)
                .ThenInclude(tenancy => tenancy!.Apartment)
                .Where(member =>
                    authorizedApartmentIds.Contains(member.Tenancy!.ApartmentId) &&
                    member.Tenancy.StartDate <= now &&
                    (!member.Tenancy.EndDate.HasValue || member.Tenancy.EndDate.Value >= now) &&
                    (!member.Tenancy.TerminatedAt.HasValue || member.Tenancy.TerminatedAt.Value > now))
                .ToListAsync();
            var targets = memberships
                .Where(member => member.Member != null && member.MemberId != userId)
                .GroupBy(member => member.MemberId)
                .Select(group => group.OrderByDescending(member => member.Tenancy!.StartDate).First())
                .ToList();
            if (targets.Count == 0)
            {
                return BadRequest(new { Message = "This property does not currently have any tenants who can receive the announcement." });
            }

            var createdMessages = new List<ApartmentConversation>();
            foreach (var target in targets)
            {
                var apartment = target.Tenancy!.Apartment!;
                apartment.Property = property;
                var conversation = await GetOrCreateConversationAsync(apartment, target.MemberId);
                await AddMessageAsync(conversation, sender, request.Message.Trim(), true, true, null);
                createdMessages.Add(conversation);
            }

            return Ok(new { RecipientCount = createdMessages.Count });
        }

        private async Task<List<ApartmentConversation>> LoadVisibleConversationsAsync(string userId)
        {
            var user = await _userManager.FindByIdAsync(userId);
            var isAdmin = user != null && await _userManager.IsInRoleAsync(user, "Admin");
            var propertyIds = await _context.Properties
                .Where(property => property.LandlordId == userId)
                .Select(property => property.Id)
                .ToListAsync();
            var managedPropertyIds = await _context.PropertyManagerAssignments
                .Where(assignment => assignment.ManagerId == userId)
                .Select(assignment => assignment.PropertyId)
                .ToListAsync();
            propertyIds.AddRange(managedPropertyIds);
            propertyIds = propertyIds.Distinct().ToList();

            var candidates = await _context.ApartmentConversations
                .Include(c => c.Apartment)
                .ThenInclude(a => a!.Property)
                .Include(c => c.Visitor)
                .Include(c => c.Landlord)
                .Where(c => (!c.IsPropertyTeamConversation && c.VisitorId == userId) ||
                            propertyIds.Contains(c.Apartment!.PropertyId) ||
                            (isAdmin && c.IsPropertyTeamConversation))
                .OrderByDescending(c => c.LastMessageAt)
                .ToListAsync();

            var visible = new List<ApartmentConversation>();
            foreach (var conversation in candidates)
            {
                if ((!conversation.IsPropertyTeamConversation && conversation.VisitorId == userId) ||
                    conversation.Apartment?.Property?.LandlordId == userId ||
                    (isAdmin && conversation.IsPropertyTeamConversation))
                {
                    visible.Add(conversation);
                    continue;
                }

                if (await _permissionService.HasApartmentPermissionAsync(
                        userId, conversation.ApartmentId, ManagerPermission.ViewMessages, false))
                {
                    visible.Add(conversation);
                }
            }

            return visible;
        }

        private async Task<ApartmentConversation> GetOrCreateConversationAsync(Apartment apartment, string residentId)
        {
            var conversation = await _context.ApartmentConversations
                .FirstOrDefaultAsync(c => c.ApartmentId == apartment.Id && c.VisitorId == residentId && !c.IsPropertyTeamConversation);
            if (conversation != null)
            {
                conversation.Apartment ??= apartment;
                return conversation;
            }

            conversation = new ApartmentConversation
            {
                ApartmentId = apartment.Id,
                Apartment = apartment,
                LandlordId = apartment.Property!.LandlordId,
                VisitorId = residentId,
                IsPropertyTeamConversation = false,
                CreatedAt = DateTimeOffset.UtcNow,
                LastMessageAt = DateTimeOffset.UtcNow
            };
            _context.ApartmentConversations.Add(conversation);
            await _context.SaveChangesAsync();
            return conversation;
        }

        private async Task<ApartmentConversation> GetOrCreatePropertyTeamConversationAsync(Apartment apartment)
        {
            var conversation = await _context.ApartmentConversations
                .FirstOrDefaultAsync(c => c.Apartment!.PropertyId == apartment.PropertyId && c.IsPropertyTeamConversation);
            if (conversation != null) return conversation;
            conversation = new ApartmentConversation
            {
                ApartmentId = apartment.Id,
                Apartment = apartment,
                LandlordId = apartment.Property!.LandlordId,
                VisitorId = apartment.Property.LandlordId,
                IsPropertyTeamConversation = true,
                CreatedAt = DateTimeOffset.UtcNow,
                LastMessageAt = DateTimeOffset.UtcNow
            };
            _context.ApartmentConversations.Add(conversation);
            await _context.SaveChangesAsync();
            return conversation;
        }

        private async Task AddMessageAsync(
            ApartmentConversation conversation,
            ApplicationUser sender,
            string body,
            bool isPropertyBroadcast,
            bool notifyRecipients,
            int? replyToMessageId)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                throw new InvalidOperationException("Message cannot be empty.");
            }

            var now = DateTimeOffset.UtcNow;
            var message = new ConversationMessage
            {
                ConversationId = conversation.Id,
                SenderId = sender.Id,
                Body = body,
                IsPropertyBroadcast = isPropertyBroadcast,
                ReplyToMessageId = replyToMessageId,
                CreatedAt = now
            };
            _context.ConversationMessages.Add(message);
            conversation.LastMessageAt = now;
            var isResident = !conversation.IsPropertyTeamConversation && string.Equals(sender.Id, conversation.VisitorId, StringComparison.Ordinal);
            if (isResident)
            {
                conversation.LastVisitorMessageAt = now;
                conversation.VisitorLastReadAt = now;
            }
            else
            {
                conversation.LastLandlordMessageAt = now;
                conversation.LandlordLastReadAt = now;
            }

            await UpsertReadStateAsync(conversation.Id, sender.Id, now);
            await _context.SaveChangesAsync();

            if (notifyRecipients)
            {
                _backgroundJobs.Enqueue<IConversationNotificationJob>(job => job.SendMessageNotificationsAsync(message.Id));
            }
        }

        private Task<bool> IsValidReplyTargetAsync(int conversationId, int messageId)
        {
            return _context.ConversationMessages
                .AnyAsync(message => message.Id == messageId && message.ConversationId == conversationId);
        }

        private async Task<bool> HasActiveTenancyMembershipAsync(string userId, int apartmentId)
        {
            var now = DateTimeOffset.UtcNow;
            return await _context.Tenancies.AnyAsync(tenancy =>
                tenancy.ApartmentId == apartmentId &&
                tenancy.StartDate <= now &&
                (!tenancy.EndDate.HasValue || tenancy.EndDate.Value >= now) &&
                (!tenancy.TerminatedAt.HasValue || tenancy.TerminatedAt.Value > now) &&
                tenancy.Members.Any(member => member.MemberId == userId));
        }

        private async Task MarkConversationReadAsync(ApartmentConversation conversation, string userId, bool isResident)
        {
            var now = DateTimeOffset.UtcNow;
            await UpsertReadStateAsync(conversation.Id, userId, now);
            if (isResident)
            {
                conversation.VisitorLastReadAt = now;
            }
            else if (conversation.Apartment?.Property?.LandlordId == userId)
            {
                conversation.LandlordLastReadAt = now;
            }

            await _context.SaveChangesAsync();
        }

        private async Task UpsertReadStateAsync(int conversationId, string userId, DateTimeOffset readAt)
        {
            var state = await _context.ConversationReadStates
                .FirstOrDefaultAsync(s => s.ConversationId == conversationId && s.UserId == userId);
            if (state == null)
            {
                _context.ConversationReadStates.Add(new ConversationReadState
                {
                    ConversationId = conversationId,
                    UserId = userId,
                    LastReadAt = readAt
                });
            }
            else
            {
                state.LastReadAt = readAt;
            }
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
                .Include(c => c.Messages)
                .ThenInclude(m => m.ReplyToMessage)
                .ThenInclude(message => message!.Sender)
                .Include(c => c.ReadStates)
                .ThenInclude(state => state.User)
                .FirstOrDefaultAsync(c => c.Id == conversationId);
        }

        private async Task<ConversationAccess> ResolveConversationAccessAsync(
            ApartmentConversation conversation,
            string userId,
            ManagerPermission managerPermission)
        {
            if (conversation.IsPropertyTeamConversation)
            {
                var user = await _userManager.FindByIdAsync(userId);
                if (user != null && await _userManager.IsInRoleAsync(user, "Admin"))
                {
                    return new ConversationAccess(true, false);
                }
                if (string.Equals(conversation.Apartment?.Property?.LandlordId, userId, StringComparison.Ordinal))
                {
                    return new ConversationAccess(true, false);
                }
                var propertyId = conversation.Apartment?.PropertyId ?? 0;
                var managerAccess = propertyId > 0 && await _permissionService.HasPropertyPermissionAsync(
                    userId, propertyId, managerPermission, false);
                return new ConversationAccess(managerAccess, false);
            }

            var isResident = string.Equals(conversation.VisitorId, userId, StringComparison.Ordinal);
            if (isResident)
            {
                return new ConversationAccess(true, true);
            }

            if (string.Equals(conversation.Apartment?.Property?.LandlordId, userId, StringComparison.Ordinal))
            {
                return new ConversationAccess(true, false);
            }

            var isAuthorizedManager = await _permissionService.HasApartmentPermissionAsync(
                userId, conversation.ApartmentId, managerPermission, false);
            return new ConversationAccess(isAuthorizedManager, false);
        }

        private async Task<ConversationThreadDto> BuildThreadDtoAsync(
            ApartmentConversation conversation,
            string currentUserId,
            bool isResident,
            bool canReply)
        {
            var leadImageMap = await BuildLeadImageMapAsync(new List<Apartment> { conversation.Apartment! });
            var propertyName = conversation.Apartment?.Property?.Name ?? string.Empty;
            var readStates = conversation.ReadStates.ToList();
            var roleLabels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var state in readStates.Where(state => state.User != null))
            {
                if (state.UserId == conversation.Apartment?.Property?.LandlordId)
                {
                    roleLabels[state.UserId] = "Landlord";
                }
                else if (!conversation.IsPropertyTeamConversation && state.UserId == conversation.VisitorId)
                {
                    roleLabels[state.UserId] = "Tenant";
                }
                else if (await _userManager.IsInRoleAsync(state.User!, "Admin"))
                {
                    roleLabels[state.UserId] = "Administrator";
                }
                else
                {
                    roleLabels[state.UserId] = "Manager";
                }
            }

            string ResolveSenderName(ConversationMessage message)
            {
                var isPropertyTeamMessage = conversation.IsPropertyTeamConversation ||
                                            !string.Equals(message.SenderId, conversation.VisitorId, StringComparison.Ordinal);
                return isResident && isPropertyTeamMessage && !conversation.IsPropertyTeamConversation
                    ? $"{propertyName} team"
                    : (message.Sender?.FullName ?? message.Sender?.Email ?? "User");
            }

            return new ConversationThreadDto
            {
                ConversationId = conversation.Id,
                ApartmentId = conversation.ApartmentId,
                PropertyId = conversation.Apartment?.PropertyId ?? 0,
                ApartmentName = conversation.IsPropertyTeamConversation ? string.Empty : conversation.Apartment?.Name ?? string.Empty,
                PropertyName = propertyName,
                City = conversation.Apartment?.Property?.City ?? string.Empty,
                Address = conversation.Apartment?.Property?.Address ?? string.Empty,
                Status = conversation.Apartment?.Status.ToString() ?? string.Empty,
                Price = conversation.Apartment?.Price ?? 0,
                LandlordName = conversation.Landlord?.FullName ?? conversation.Landlord?.Email ?? "Landlord",
                VisitorName = conversation.Visitor?.FullName ?? conversation.Visitor?.Email ?? "Tenant",
                CounterpartyName = conversation.IsPropertyTeamConversation
                    ? $"{propertyName} internal team"
                    : isResident
                    ? $"{propertyName} team"
                    : (conversation.Visitor?.FullName ?? conversation.Visitor?.Email ?? "Tenant"),
                LeadImageUrl = leadImageMap.TryGetValue(conversation.ApartmentId, out var leadImage) ? leadImage : null,
                CreatedAt = conversation.CreatedAt,
                LastMessageAt = conversation.LastMessageAt,
                CanReply = canReply,
                IsResidentView = isResident,
                IsPropertyTeamConversation = conversation.IsPropertyTeamConversation,
                Messages = conversation.Messages.OrderBy(m => m.CreatedAt).Select(message =>
                {
                    var sentByCurrentUser = string.Equals(message.SenderId, currentUserId, StringComparison.Ordinal);
                    var isPropertyTeamMessage = conversation.IsPropertyTeamConversation ||
                                                !string.Equals(message.SenderId, conversation.VisitorId, StringComparison.Ordinal);
                    var isRead = !sentByCurrentUser || (isResident
                        ? readStates.Any(state => state.UserId != currentUserId && state.LastReadAt >= message.CreatedAt)
                        : readStates.Any(state => state.UserId == conversation.VisitorId && state.LastReadAt >= message.CreatedAt));
                    var senderName = ResolveSenderName(message);
                    var readBy = sentByCurrentUser
                        ? readStates
                            .Where(state => state.UserId != message.SenderId && state.LastReadAt >= message.CreatedAt)
                            .OrderBy(state => state.LastReadAt)
                            .Select(state => new ConversationReadReceiptDto
                            {
                                UserName = state.User?.FullName ?? state.User?.Email ?? "User",
                                RoleLabel = roleLabels.GetValueOrDefault(state.UserId, "Member"),
                                ReadAt = state.LastReadAt
                            }).ToList()
                        : new List<ConversationReadReceiptDto>();

                    return new ConversationMessageDto
                    {
                        MessageId = message.Id,
                        SenderId = message.SenderId,
                        SenderName = senderName,
                        Body = message.Body,
                        SentAt = message.CreatedAt,
                        SentByCurrentUser = sentByCurrentUser,
                        IsRead = isRead,
                        IsPropertyBroadcast = message.IsPropertyBroadcast,
                        IsPropertyTeamMessage = isPropertyTeamMessage,
                        ReplyToMessageId = message.ReplyToMessageId,
                        ReplyToSenderName = message.ReplyToMessage == null ? null : ResolveSenderName(message.ReplyToMessage),
                        ReplyToBody = message.ReplyToMessage?.Body,
                        SenderContextLabel = conversation.IsPropertyTeamConversation
                            ? "Internal property team"
                            : message.IsPropertyBroadcast
                            ? "Property announcement"
                            : isPropertyTeamMessage ? "Property team" : "Tenant",
                        ReadBy = readBy
                    };
                }).ToList()
            };
        }

        private static DateTimeOffset? ResolveLastReadAt(
            ApartmentConversation conversation,
            string userId,
            bool isResident,
            IReadOnlyDictionary<int, DateTimeOffset> readStates)
        {
            if (readStates.TryGetValue(conversation.Id, out var readAt))
            {
                return readAt;
            }

            if (isResident)
            {
                return conversation.VisitorLastReadAt;
            }

            return string.Equals(conversation.Apartment?.Property?.LandlordId, userId, StringComparison.Ordinal)
                ? conversation.LandlordLastReadAt
                : null;
        }

        private async Task<Dictionary<int, string>> BuildLeadImageMapAsync(IReadOnlyCollection<Apartment> apartments)
        {
            if (apartments.Count == 0)
            {
                return new Dictionary<int, string>();
            }

            var apartmentIds = apartments.Select(a => a.Id).Distinct().ToList();
            var propertyIds = apartments.Select(a => a.PropertyId).Distinct().ToList();
            var apartmentImages = await _context.Documents
                .Where(d => d.ApartmentId != null && apartmentIds.Contains(d.ApartmentId.Value) && d.DocumentType == DocumentTypeEnum.ApartmentImage)
                .GroupBy(d => d.ApartmentId!.Value)
                .Select(g => g.OrderByDescending(d => d.CreatedAt).First())
                .ToListAsync();
            var propertyImages = await _context.Documents
                .Where(d => d.PropertyId != null && propertyIds.Contains(d.PropertyId.Value) && d.ApartmentId == null && d.DocumentType == DocumentTypeEnum.PropertyImage)
                .GroupBy(d => d.PropertyId!.Value)
                .Select(g => g.OrderByDescending(d => d.CreatedAt).First())
                .ToListAsync();

            var apartmentImageUrls = new Dictionary<int, string>();
            foreach (var image in apartmentImages)
            {
                apartmentImageUrls[image.ApartmentId!.Value] = await _storageService.GetReadUrlAsync(image.BlobUrl);
            }

            var propertyImageUrls = new Dictionary<int, string>();
            foreach (var image in propertyImages)
            {
                propertyImageUrls[image.PropertyId!.Value] = await _storageService.GetReadUrlAsync(image.BlobUrl);
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

        private sealed record ConversationAccess(bool CanAccess, bool IsResident);
        private sealed record ConversationApartmentScope(
            Apartment Apartment,
            bool IsResidentApartment,
            bool CanBroadcast,
            bool CanUseTeamConversation);
    }
}
