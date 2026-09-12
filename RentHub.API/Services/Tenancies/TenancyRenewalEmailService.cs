using System.Globalization;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Email;
using RentHub.API.Services.Messaging;

namespace RentHub.API.Services.Tenancies
{
    public interface ITenancyRenewalEmailService
    {
        Task SendExpiryReminderAsync(ApplicationUser tenant, Tenancy tenancy, CancellationToken cancellationToken = default);
        Task SendRequestSubmittedAsync(TenancyExtensionRequest request, CancellationToken cancellationToken = default);
        Task SendRequestWithdrawnAsync(TenancyExtensionRequest request, CancellationToken cancellationToken = default);
        Task SendDecisionAsync(TenancyExtensionRequest request, CancellationToken cancellationToken = default);
    }

    public class TenancyRenewalEmailService : ITenancyRenewalEmailService
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;
        private readonly INotificationDeliveryService _notificationDeliveryService;

        public TenancyRenewalEmailService(
            ApplicationDbContext context,
            IEmailService emailService,
            INotificationDeliveryService notificationDeliveryService,
            IConfiguration configuration)
        {
            _context = context;
            _emailService = emailService;
            _notificationDeliveryService = notificationDeliveryService;
            _configuration = configuration;
        }

        public async Task SendExpiryReminderAsync(
            ApplicationUser tenant,
            Tenancy tenancy,
            CancellationToken cancellationToken = default)
        {
            if (!tenancy.EndDate.HasValue)
            {
                return;
            }

            var propertyName = tenancy.Apartment?.Property?.Name ?? string.Empty;
            var apartmentName = tenancy.Apartment?.Name ?? string.Empty;
            var renewalUrl = BuildPortalUrl($"/TenancyRequests?tenancyId={tenancy.Id}&requestType=Renewal");
            var endDate = FormatDate(tenancy.EndDate.Value, tenant.EmailLanguage);
            var name = DisplayName(tenant);

            var subject = tenant.EmailLanguage == PlatformLanguage.French
                ? $"Votre bail arrive bientôt à échéance – {apartmentName}"
                : $"Your tenancy is ending soon - {apartmentName}";
            var body = tenant.EmailLanguage == PlatformLanguage.French
                ? string.Join(Environment.NewLine, new[]
                {
                    $"Bonjour {name},",
                    string.Empty,
                    $"Votre bail pour {propertyName} – {apartmentName} doit prendre fin le {endDate}.",
                    "Si vous souhaitez rester dans le logement, vous pouvez demander un renouvellement et proposer une nouvelle date de fin.",
                    $"Demander un renouvellement : {renewalUrl}",
                    string.Empty,
                    "Merci,",
                    "Lontsi Homes"
                })
                : string.Join(Environment.NewLine, new[]
                {
                    $"Hello {name},",
                    string.Empty,
                    $"Your tenancy for {propertyName} - {apartmentName} is scheduled to end on {endDate}.",
                    "If you would like to stay, you can request a renewal and propose a new end date.",
                    $"Request a renewal: {renewalUrl}",
                    string.Empty,
                    "Thank you,",
                    "Lontsi Homes"
                });

            if (!string.IsNullOrWhiteSpace(tenant.Email))
            {
                await _emailService.SendEmailAsync(tenant.Email, subject, body);
            }
            await _notificationDeliveryService.EnqueueWhatsAppAsync(
                new EnqueueWhatsAppNotification(
                    "tenancy_ending_soon",
                    tenant.Id,
                    new[] { name, propertyName, apartmentName, endDate },
                    $"tenancy:{tenancy.Id}:ending:{tenancy.EndDate.Value.UtcTicks}:{tenant.Id}:whatsapp",
                    WhatsAppTemplateValues.UrlButton(_configuration, "tenancy_ending_soon", "tenancyId",
                        tenancy.Id.ToString(CultureInfo.InvariantCulture)),
                    RelatedEntityId: tenancy.Id.ToString()),
                cancellationToken);
        }

        public async Task SendRequestSubmittedAsync(
            TenancyExtensionRequest request,
            CancellationToken cancellationToken = default)
        {
            var tenancy = request.Tenancy;
            var property = tenancy?.Apartment?.Property;
            if (tenancy?.Apartment == null || property == null)
            {
                return;
            }

            var recipientIds = new HashSet<string>(StringComparer.Ordinal)
            {
                property.LandlordId
            };

            var managerAssignments = await _context.PropertyManagerAssignments
                .AsNoTracking()
                .Include(assignment => assignment.ApartmentOverrides)
                .Where(assignment =>
                    !assignment.IsDeleted &&
                    assignment.PropertyId == property.Id)
                .ToListAsync(cancellationToken);
            var managerIds = managerAssignments
                .Where(assignment =>
                {
                    var apartmentOverride = assignment.ApartmentOverrides
                        .FirstOrDefault(item => item.ApartmentId == tenancy.ApartmentId);
                    var hasApartmentAccess = apartmentOverride?.HasAccess ?? assignment.AccessAllApartments;
                    var effectiveFlags = assignment.PermissionFlags |
                                         (apartmentOverride?.AllowedPermissionFlags ?? 0L);
                    effectiveFlags &= ~(apartmentOverride?.DeniedPermissionFlags ?? 0L);
                    return hasApartmentAccess &&
                           (effectiveFlags & (long)ManagerPermission.RenewTenancy) != 0;
                })
                .Select(assignment => assignment.ManagerId)
                .ToList();
            recipientIds.UnionWith(managerIds);

            var ownerIds = await _context.ApartmentOwners
                .AsNoTracking()
                .Where(assignment =>
                    !assignment.IsDeleted &&
                    assignment.ApartmentId == tenancy.ApartmentId &&
                    assignment.Permission == PermissionLevelEnum.ReadWrite)
                .Select(assignment => assignment.OwnerId)
                .ToListAsync(cancellationToken);
            recipientIds.UnionWith(ownerIds);

            var recipients = await _context.Users
                .AsNoTracking()
                .Where(user => recipientIds.Contains(user.Id))
                .ToListAsync(cancellationToken);
            var reviewUrl = BuildPortalUrl($"/TenancyRequests?tenancyId={tenancy.Id}&requestType=Renewal");
            var requesterName = request.RequestedBy == null ? "Tenant" : DisplayName(request.RequestedBy);

            foreach (var recipient in recipients.DistinctBy(user => user.Id))
            {
                var proposedDate = FormatDate(request.ProposedEndDate, recipient.EmailLanguage);
                var subject = recipient.EmailLanguage == PlatformLanguage.French
                    ? $"Nouvelle demande de renouvellement – {tenancy.Apartment.Name}"
                    : $"New tenancy renewal request - {tenancy.Apartment.Name}";
                var body = recipient.EmailLanguage == PlatformLanguage.French
                    ? string.Join(Environment.NewLine, new[]
                    {
                        $"Bonjour {DisplayName(recipient)},",
                        string.Empty,
                        $"{requesterName} a demandé le renouvellement du bail pour {property.Name} – {tenancy.Apartment.Name}.",
                        $"Nouvelle date de fin proposée : {proposedDate}.",
                        $"Examiner la demande : {reviewUrl}",
                        string.Empty,
                        "Lontsi Homes"
                    })
                    : string.Join(Environment.NewLine, new[]
                    {
                        $"Hello {DisplayName(recipient)},",
                        string.Empty,
                        $"{requesterName} requested a tenancy renewal for {property.Name} - {tenancy.Apartment.Name}.",
                        $"Proposed new end date: {proposedDate}.",
                        $"Review the request: {reviewUrl}",
                        string.Empty,
                        "Lontsi Homes"
                    });

                if (!string.IsNullOrWhiteSpace(recipient.Email))
                {
                    await _emailService.SendEmailAsync(recipient.Email, subject, body);
                }

                await QueueRequestReceivedAsync(request.Id, recipient, requesterName, property.Name,
                    tenancy.Apartment.Name, "renewal", cancellationToken);
            }
        }

        public async Task SendDecisionAsync(
            TenancyExtensionRequest request,
            CancellationToken cancellationToken = default)
        {
            var tenancy = request.Tenancy;
            var property = tenancy?.Apartment?.Property;
            if (request.RequestedBy == null || tenancy?.Apartment == null || property == null)
            {
                return;
            }

            var tenant = request.RequestedBy;
            var approved = request.Status == TenancyExtensionStatusEnum.Approved;
            var recipientIds = new HashSet<string>(StringComparer.Ordinal) { tenant.Id };
            if (!string.Equals(request.ApprovedById, property.LandlordId, StringComparison.Ordinal))
            {
                recipientIds.Add(property.LandlordId);
            }

            var recipients = await _context.Users
                .AsNoTracking()
                .Where(user => recipientIds.Contains(user.Id))
                .ToListAsync(cancellationToken);
            var reviewer = string.IsNullOrWhiteSpace(request.ApprovedById)
                ? null
                : await _context.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == request.ApprovedById, cancellationToken);
            var renewalUrl = BuildPortalUrl($"/TenancyRequests?tenancyId={request.TenancyId}&requestType=Renewal");

            foreach (var recipient in recipients.DistinctBy(user => user.Id))
            {
                var french = recipient.EmailLanguage == PlatformLanguage.French;
                var proposedDate = FormatDate(request.ProposedEndDate, recipient.EmailLanguage);
                var isLandlordNotice = recipient.Id == property.LandlordId && recipient.Id != tenant.Id;
                var subject = french
                    ? approved ? "Demande de renouvellement approuvée" : "Demande de renouvellement refusée"
                    : approved ? "Tenancy renewal request approved" : "Tenancy renewal request rejected";
                var lines = new List<string>
                {
                    french ? $"Bonjour {DisplayName(recipient)}," : $"Hello {DisplayName(recipient)},",
                    string.Empty,
                    isLandlordNotice
                        ? french
                            ? $"{DisplayName(reviewer ?? recipient)} a {(approved ? "approuvé" : "refusé")} la demande de renouvellement pour {tenancy.Apartment.Name}."
                            : $"{DisplayName(reviewer ?? recipient)} {(approved ? "approved" : "rejected")} the renewal request for {tenancy.Apartment.Name}."
                        : french
                            ? approved
                                ? $"Votre demande de renouvellement pour {tenancy.Apartment.Name} a été approuvée. La nouvelle date de fin est le {proposedDate}."
                                : $"Votre demande de renouvellement pour {tenancy.Apartment.Name} a été refusée. Motif : {request.RejectionReason}"
                            : approved
                                ? $"Your renewal request for {tenancy.Apartment.Name} was approved. The new end date is {proposedDate}."
                                : $"Your renewal request for {tenancy.Apartment.Name} was rejected. Reason: {request.RejectionReason}",
                    french ? $"Consulter la demande : {renewalUrl}" : $"View the request: {renewalUrl}",
                    string.Empty,
                    "Lontsi Homes"
                };

                if (!string.IsNullOrWhiteSpace(recipient.Email))
                {
                    await _emailService.SendEmailAsync(recipient.Email, subject, string.Join(Environment.NewLine, lines));
                }

                await QueueStatusUpdateAsync(request.Id, recipient, requesterName: DisplayName(tenant),
                    property.Name, tenancy.Apartment.Name, approved ? "approved" : "rejected", cancellationToken);
            }
        }

        public async Task SendRequestWithdrawnAsync(
            TenancyExtensionRequest request,
            CancellationToken cancellationToken = default)
        {
            var tenancy = request.Tenancy;
            var property = tenancy?.Apartment?.Property;
            if (tenancy?.Apartment == null || property == null) return;

            var recipientIds = new HashSet<string>(StringComparer.Ordinal) { property.LandlordId };
            var assignments = await _context.PropertyManagerAssignments
                .AsNoTracking()
                .Include(assignment => assignment.ApartmentOverrides)
                .Where(assignment => !assignment.IsDeleted && assignment.PropertyId == property.Id)
                .ToListAsync(cancellationToken);
            recipientIds.UnionWith(assignments
                .Where(assignment =>
                {
                    var apartmentOverride = assignment.ApartmentOverrides
                        .FirstOrDefault(item => item.ApartmentId == tenancy.ApartmentId);
                    var hasApartmentAccess = apartmentOverride?.HasAccess ?? assignment.AccessAllApartments;
                    var effectiveFlags = assignment.PermissionFlags |
                                         (apartmentOverride?.AllowedPermissionFlags ?? 0L);
                    effectiveFlags &= ~(apartmentOverride?.DeniedPermissionFlags ?? 0L);
                    return hasApartmentAccess &&
                           (effectiveFlags & (long)ManagerPermission.RenewTenancy) != 0;
                })
                .Select(assignment => assignment.ManagerId));
            recipientIds.UnionWith(await _context.ApartmentOwners
                .AsNoTracking()
                .Where(owner =>
                    !owner.IsDeleted &&
                    owner.ApartmentId == tenancy.ApartmentId &&
                    owner.Permission == PermissionLevelEnum.ReadWrite)
                .Select(owner => owner.OwnerId)
                .ToListAsync(cancellationToken));
            recipientIds.Remove(request.RequestedById);

            var recipients = await _context.Users
                .AsNoTracking()
                .Where(user => recipientIds.Contains(user.Id))
                .ToListAsync(cancellationToken);
            var requestsUrl = BuildPortalUrl($"/TenancyRequests?tenancyId={request.TenancyId}&requestType=Renewal");
            var requesterName = request.RequestedBy == null ? "Tenant" : DisplayName(request.RequestedBy);

            foreach (var recipient in recipients.DistinctBy(user => user.Id))
            {
                var french = recipient.EmailLanguage == PlatformLanguage.French;
                var subject = french
                    ? $"Demande de renouvellement annulée – {tenancy.Apartment.Name}"
                    : $"Renewal request cancelled - {tenancy.Apartment.Name}";
                var lines = french
                    ? new[]
                    {
                        $"Bonjour {DisplayName(recipient)},", string.Empty,
                        $"{requesterName} a annulé sa demande de renouvellement pour {property.Name} – {tenancy.Apartment.Name} avant qu’une décision soit prise.",
                        $"Consulter le registre : {requestsUrl}", string.Empty, "Lontsi Homes"
                    }
                    : new[]
                    {
                        $"Hello {DisplayName(recipient)},", string.Empty,
                        $"{requesterName} cancelled their renewal request for {property.Name} - {tenancy.Apartment.Name} before a decision was made.",
                        $"View the register: {requestsUrl}", string.Empty, "Lontsi Homes"
                    };

                if (!string.IsNullOrWhiteSpace(recipient.Email))
                {
                    await _emailService.SendEmailAsync(recipient.Email, subject, string.Join(Environment.NewLine, lines));
                }

                await QueueStatusUpdateAsync(request.Id, recipient, requesterName,
                    property.Name, tenancy.Apartment.Name, "cancelled", cancellationToken);
            }
        }

        private Task<long?> QueueRequestReceivedAsync(
            int requestId,
            ApplicationUser recipient,
            string requesterName,
            string propertyName,
            string apartmentName,
            string requestType,
            CancellationToken cancellationToken)
        {
            return _notificationDeliveryService.EnqueueWhatsAppAsync(
                new EnqueueWhatsAppNotification(
                    "tenancy_request_received",
                    recipient.Id,
                    new[] { DisplayName(recipient), requesterName, WhatsAppTemplateValues.RequestType(requestType, recipient.EmailLanguage), $"{propertyName} — {apartmentName}" },
                    $"tenancy-request:renewal:{requestId}:received:{recipient.Id}:whatsapp",
                    RelatedEntityId: requestId.ToString()),
                cancellationToken);
        }

        private Task<long?> QueueStatusUpdateAsync(
            int requestId,
            ApplicationUser recipient,
            string requesterName,
            string propertyName,
            string apartmentName,
            string status,
            CancellationToken cancellationToken)
        {
            return _notificationDeliveryService.EnqueueWhatsAppAsync(
                new EnqueueWhatsAppNotification(
                    "tenancy_request_status_update",
                    recipient.Id,
                    new[] { DisplayName(recipient), WhatsAppTemplateValues.RequestType("renewal", recipient.EmailLanguage), requesterName, $"{propertyName} — {apartmentName}", WhatsAppTemplateValues.RequestStatus(status, recipient.EmailLanguage) },
                    $"tenancy-request:renewal:{requestId}:status:{status}:{recipient.Id}:whatsapp",
                    RelatedEntityId: requestId.ToString()),
                cancellationToken);
        }

        private string BuildPortalUrl(string path)
        {
            var portalBaseUrl = (_configuration["Portal:BaseUrl"] ?? "https://localhost:7059").Trim().TrimEnd('/');
            return $"{portalBaseUrl}{path}";
        }

        private static string DisplayName(ApplicationUser user)
            => string.IsNullOrWhiteSpace(user.FullName) ? user.Email ?? "there" : user.FullName.Trim();

        private static string FormatDate(DateTimeOffset value, PlatformLanguage language)
            => value.ToString("d", CultureInfo.GetCultureInfo(language.ToCultureName()));
    }
}
