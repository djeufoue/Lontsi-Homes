using System.Globalization;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using LontsiHomes.API.Data;
using LontsiHomes.API.Models.Entities;
using LontsiHomes.API.Services.Email;
using LontsiHomes.API.Services.Messaging;

namespace LontsiHomes.API.Services.Tenancies
{
    public interface ITenancyTerminationEmailService
    {
        Task SendRequestSubmittedAsync(TenancyTerminationRequest request, CancellationToken cancellationToken = default);
        Task SendRequestWithdrawnAsync(TenancyTerminationRequest request, CancellationToken cancellationToken = default);
        Task SendDecisionAsync(TenancyTerminationRequest request, CancellationToken cancellationToken = default);
        Task SendDirectDecisionAsync(TenancyTerminationRequest request, CancellationToken cancellationToken = default);
        Task SendCancellationAsync(TenancyTerminationRequest request, CancellationToken cancellationToken = default);
    }

    public sealed class TenancyTerminationEmailService : ITenancyTerminationEmailService
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;
        private readonly INotificationDeliveryService _notificationDeliveryService;

        public TenancyTerminationEmailService(
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

        public async Task SendRequestSubmittedAsync(
            TenancyTerminationRequest request,
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
                .Where(assignment => HasPermissionForApartment(
                    assignment,
                    tenancy.ApartmentId,
                    ManagerPermission.TerminateTenancy))
                .Select(assignment => assignment.ManagerId));

            recipientIds.UnionWith(await _context.ApartmentOwners
                .AsNoTracking()
                .Where(owner =>
                    !owner.IsDeleted &&
                    owner.ApartmentId == tenancy.ApartmentId &&
                    owner.Permission == PermissionLevelEnum.ReadWrite)
                .Select(owner => owner.OwnerId)
                .ToListAsync(cancellationToken));

            var recipients = await _context.Users
                .AsNoTracking()
                .Where(user => recipientIds.Contains(user.Id))
                .ToListAsync(cancellationToken);
            var requestsUrl = BuildPortalUrl("/TenancyRequests");
            var requesterName = request.RequestedBy == null ? "Tenant" : DisplayName(request.RequestedBy);

            foreach (var recipient in recipients.DistinctBy(user => user.Id))
            {
                var requestedDate = FormatDate(request.RequestedEndDate, recipient.EmailLanguage);
                var french = recipient.EmailLanguage == PlatformLanguage.French;
                var subject = french
                    ? $"Nouvelle demande de fin de bail – {tenancy.Apartment.Name}"
                    : $"New tenancy end request - {tenancy.Apartment.Name}";
                var lines = french
                    ? new List<string>
                    {
                        $"Bonjour {DisplayName(recipient)},", string.Empty,
                        $"{requesterName} demande à mettre fin au bail pour {property.Name} – {tenancy.Apartment.Name} le {requestedDate}.",
                        string.IsNullOrWhiteSpace(request.Reason) ? string.Empty : $"Motif fourni : {request.Reason}",
                        $"Examiner la demande : {requestsUrl}", string.Empty, "Lontsi Homes"
                    }
                    : new List<string>
                    {
                        $"Hello {DisplayName(recipient)},", string.Empty,
                        $"{requesterName} requested to end the tenancy for {property.Name} - {tenancy.Apartment.Name} on {requestedDate}.",
                        string.IsNullOrWhiteSpace(request.Reason) ? string.Empty : $"Reason provided: {request.Reason}",
                        $"Review the request: {requestsUrl}", string.Empty, "Lontsi Homes"
                    };

                if (!string.IsNullOrWhiteSpace(recipient.Email))
                {
                    await _emailService.SendEmailAsync(recipient.Email, subject, string.Join(Environment.NewLine, lines));
                }

                await QueueRequestReceivedAsync(request.Id, recipient, requesterName,
                    property.Name, tenancy.Apartment.Name, cancellationToken);
            }
        }

        public async Task SendDecisionAsync(
            TenancyTerminationRequest request,
            CancellationToken cancellationToken = default)
        {
            var tenancy = request.Tenancy;
            var property = tenancy?.Apartment?.Property;
            if (tenancy?.Apartment == null || property == null) return;

            var recipientIds = new HashSet<string>(StringComparer.Ordinal) { request.RequestedById };
            if (!string.Equals(request.ReviewedById, property.LandlordId, StringComparison.Ordinal))
            {
                recipientIds.Add(property.LandlordId);
            }

            var recipients = await _context.Users
                .AsNoTracking()
                .Where(user => recipientIds.Contains(user.Id))
                .ToListAsync(cancellationToken);
            var reviewer = string.IsNullOrWhiteSpace(request.ReviewedById)
                ? null
                : await _context.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == request.ReviewedById, cancellationToken);
            var approved = request.Status == TenancyTerminationRequestStatusEnum.Approved;
            var requestsUrl = BuildPortalUrl("/TenancyRequests");

            foreach (var recipient in recipients.DistinctBy(user => user.Id))
            {
                var french = recipient.EmailLanguage == PlatformLanguage.French;
                var requestedDate = FormatDate(request.RequestedEndDate, recipient.EmailLanguage);
                var isLandlordNotice = recipient.Id == property.LandlordId && recipient.Id != request.RequestedById;
                var subject = french
                    ? approved ? "Demande de fin de bail approuvée" : "Demande de fin de bail refusée"
                    : approved ? "Tenancy end request approved" : "Tenancy end request rejected";
                var action = french
                    ? approved ? "approuvée" : "refusée"
                    : approved ? "approved" : "rejected";
                var lines = new List<string>
                {
                    french ? $"Bonjour {DisplayName(recipient)}," : $"Hello {DisplayName(recipient)},",
                    string.Empty,
                    isLandlordNotice
                        ? french
                            ? $"{DisplayName(reviewer ?? recipient)} a {action} la demande de fin de bail pour {tenancy.Apartment.Name}."
                            : $"{DisplayName(reviewer ?? recipient)} {action} the tenancy end request for {tenancy.Apartment.Name}."
                        : french
                            ? $"Votre demande de fin de bail pour {tenancy.Apartment.Name} a été {action}."
                            : $"Your tenancy end request for {tenancy.Apartment.Name} was {action}.",
                    approved
                        ? french ? $"La date de fin du bail est maintenant le {requestedDate}." : $"The tenancy end date is now {requestedDate}."
                        : french ? $"Le bail reste inchangé. Motif du refus : {request.RejectionReason}" : $"The tenancy remains unchanged. Rejection reason: {request.RejectionReason}",
                    french ? $"Consulter les demandes : {requestsUrl}" : $"View requests: {requestsUrl}",
                    string.Empty,
                    "Lontsi Homes"
                };

                if (!string.IsNullOrWhiteSpace(recipient.Email))
                {
                    await _emailService.SendEmailAsync(recipient.Email, subject, string.Join(Environment.NewLine, lines));
                }

                await QueueStatusUpdateAsync(request.Id, recipient, requesterName: request.RequestedBy == null ? "Tenant" : DisplayName(request.RequestedBy),
                    property.Name, tenancy.Apartment.Name, approved ? "approved" : "rejected", cancellationToken);
            }
        }

        public async Task SendRequestWithdrawnAsync(
            TenancyTerminationRequest request,
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
                .Where(assignment => HasPermissionForApartment(
                    assignment,
                    tenancy.ApartmentId,
                    ManagerPermission.TerminateTenancy))
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
            var requestsUrl = BuildPortalUrl("/TenancyRequests");
            var requesterName = request.RequestedBy == null ? "Tenant" : DisplayName(request.RequestedBy);

            foreach (var recipient in recipients.DistinctBy(user => user.Id))
            {
                var french = recipient.EmailLanguage == PlatformLanguage.French;
                var subject = french
                    ? $"Demande de fin de bail annulée – {tenancy.Apartment.Name}"
                    : $"Tenancy end request cancelled - {tenancy.Apartment.Name}";
                var lines = french
                    ? new[]
                    {
                        $"Bonjour {DisplayName(recipient)},", string.Empty,
                        $"{requesterName} a annulé sa demande de fin de bail pour {property.Name} – {tenancy.Apartment.Name} avant qu’une décision soit prise.",
                        $"Consulter le registre : {requestsUrl}", string.Empty, "Lontsi Homes"
                    }
                    : new[]
                    {
                        $"Hello {DisplayName(recipient)},", string.Empty,
                        $"{requesterName} cancelled their tenancy end request for {property.Name} - {tenancy.Apartment.Name} before a decision was made.",
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

        public async Task SendDirectDecisionAsync(
            TenancyTerminationRequest request,
            CancellationToken cancellationToken = default)
        {
            var tenancy = request.Tenancy;
            var property = tenancy?.Apartment?.Property;
            if (tenancy?.Apartment == null || property == null) return;

            var recipients = await LoadTenantAndLandlordRecipientsAsync(tenancy, property, request.RequestedById, cancellationToken);
            var actor = request.RequestedBy ?? await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(user => user.Id == request.RequestedById, cancellationToken);
            var requestsUrl = BuildPortalUrl("/TenancyRequests");

            foreach (var recipient in recipients)
            {
                var french = recipient.EmailLanguage == PlatformLanguage.French;
                var endDate = FormatDate(request.RequestedEndDate, recipient.EmailLanguage);
                var actorName = actor == null ? (french ? "Le bailleur" : "The landlord") : DisplayName(actor);
                var reason = FormatTerminationReason(tenancy.TerminationReason, french);
                var subject = french
                    ? $"Préavis de fin de bail – {tenancy.Apartment.Name}"
                    : $"Notice of tenancy termination - {tenancy.Apartment.Name}";
                var lines = french
                    ? new List<string>
                    {
                        $"Bonjour {DisplayName(recipient)},", string.Empty,
                        $"{actorName} a enregistré une décision de mettre fin au bail de {property.Name} – {tenancy.Apartment.Name} le {endDate}.",
                        $"Motif enregistré : {reason}",
                        string.IsNullOrWhiteSpace(tenancy.TerminationNotes) ? string.Empty : $"Informations fournies : {tenancy.TerminationNotes}",
                        "Le suivi des loyers et les rappels automatiques s’arrêteront à cette date. Les dettes antérieures restent visibles.",
                        $"Consulter le registre : {requestsUrl}", string.Empty, "Lontsi Homes"
                    }
                    : new List<string>
                    {
                        $"Hello {DisplayName(recipient)},", string.Empty,
                        $"{actorName} recorded a decision to end the tenancy for {property.Name} - {tenancy.Apartment.Name} on {endDate}.",
                        $"Recorded reason: {reason}",
                        string.IsNullOrWhiteSpace(tenancy.TerminationNotes) ? string.Empty : $"Information provided: {tenancy.TerminationNotes}",
                        "Rent tracking and automatic reminders will stop on that date. Earlier outstanding balances remain visible.",
                        $"View the register: {requestsUrl}", string.Empty, "Lontsi Homes"
                    };

                if (!string.IsNullOrWhiteSpace(recipient.Email))
                {
                    await _emailService.SendEmailAsync(recipient.Email, subject, string.Join(Environment.NewLine, lines));
                }

                await QueueStatusUpdateAsync(request.Id, recipient, actorName,
                    property.Name, tenancy.Apartment.Name, "approved", cancellationToken);
            }
        }

        public async Task SendCancellationAsync(
            TenancyTerminationRequest request,
            CancellationToken cancellationToken = default)
        {
            var tenancy = request.Tenancy;
            var property = tenancy?.Apartment?.Property;
            if (tenancy?.Apartment == null || property == null) return;

            var recipients = await LoadTenantAndLandlordRecipientsAsync(tenancy, property, request.UpdatedBy, cancellationToken);
            var actor = string.IsNullOrWhiteSpace(request.UpdatedBy)
                ? null
                : await _context.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == request.UpdatedBy, cancellationToken);
            var requestsUrl = BuildPortalUrl("/TenancyRequests");

            foreach (var recipient in recipients)
            {
                var french = recipient.EmailLanguage == PlatformLanguage.French;
                var actorName = actor == null ? (french ? "Une personne autorisée" : "An authorized user") : DisplayName(actor);
                var subject = french
                    ? $"Décision de fin de bail annulée – {tenancy.Apartment.Name}"
                    : $"Tenancy termination decision cancelled - {tenancy.Apartment.Name}";
                var lines = french
                    ? new List<string>
                    {
                        $"Bonjour {DisplayName(recipient)},", string.Empty,
                        $"{actorName} a annulé la décision de fin de bail pour {property.Name} – {tenancy.Apartment.Name}.",
                        string.IsNullOrWhiteSpace(request.RejectionReason) ? string.Empty : request.RejectionReason,
                        $"Consulter le registre : {requestsUrl}", string.Empty, "Lontsi Homes"
                    }
                    : new List<string>
                    {
                        $"Hello {DisplayName(recipient)},", string.Empty,
                        $"{actorName} cancelled the tenancy termination decision for {property.Name} - {tenancy.Apartment.Name}.",
                        string.IsNullOrWhiteSpace(request.RejectionReason) ? string.Empty : request.RejectionReason,
                        $"View the register: {requestsUrl}", string.Empty, "Lontsi Homes"
                    };

                if (!string.IsNullOrWhiteSpace(recipient.Email))
                {
                    await _emailService.SendEmailAsync(recipient.Email, subject, string.Join(Environment.NewLine, lines));
                }

                await QueueStatusUpdateAsync(request.Id, recipient, actorName,
                    property.Name, tenancy.Apartment.Name, "cancelled", cancellationToken);
            }
        }

        private async Task<List<ApplicationUser>> LoadTenantAndLandlordRecipientsAsync(
            Tenancy tenancy,
            Property property,
            string? actingUserId,
            CancellationToken cancellationToken)
        {
            var recipientIds = (await _context.TenancyMembers
                    .AsNoTracking()
                    .Where(member => member.TenancyId == tenancy.Id && !member.IsDeleted)
                    .Select(member => member.MemberId)
                    .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);
            if (!string.Equals(actingUserId, property.LandlordId, StringComparison.Ordinal))
            {
                recipientIds.Add(property.LandlordId);
            }

            return (await _context.Users
                    .AsNoTracking()
                    .Where(user => recipientIds.Contains(user.Id))
                    .ToListAsync(cancellationToken))
                .DistinctBy(user => user.Id)
                .ToList();
        }

        private Task<long?> QueueRequestReceivedAsync(
            int requestId,
            ApplicationUser recipient,
            string requesterName,
            string propertyName,
            string apartmentName,
            CancellationToken cancellationToken)
        {
            return _notificationDeliveryService.EnqueueWhatsAppAsync(
                new EnqueueWhatsAppNotification(
                    "tenancy_request_received",
                    recipient.Id,
                    new[] { DisplayName(recipient), requesterName, WhatsAppTemplateValues.RequestType("termination", recipient.EmailLanguage), $"{propertyName} — {apartmentName}" },
                    $"tenancy-request:termination:{requestId}:received:{recipient.Id}:whatsapp",
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
                    new[] { DisplayName(recipient), WhatsAppTemplateValues.RequestType("termination", recipient.EmailLanguage), requesterName, $"{propertyName} — {apartmentName}", WhatsAppTemplateValues.RequestStatus(status, recipient.EmailLanguage) },
                    $"tenancy-request:termination:{requestId}:status:{status}:{recipient.Id}:whatsapp",
                    RelatedEntityId: requestId.ToString()),
                cancellationToken);
        }

        private static bool HasPermissionForApartment(
            PropertyManagerAssignment assignment,
            int apartmentId,
            ManagerPermission permission)
        {
            var apartmentOverride = assignment.ApartmentOverrides.FirstOrDefault(item => item.ApartmentId == apartmentId);
            var hasAccess = apartmentOverride?.HasAccess ?? assignment.AccessAllApartments;
            var flags = assignment.PermissionFlags | (apartmentOverride?.AllowedPermissionFlags ?? 0L);
            flags &= ~(apartmentOverride?.DeniedPermissionFlags ?? 0L);
            return hasAccess && (flags & (long)permission) != 0;
        }

        private string BuildPortalUrl(string path)
        {
            var portalBaseUrl = (_configuration["Portal:BaseUrl"] ?? "https://localhost:7059").Trim().TrimEnd('/');
            return $"{portalBaseUrl}{path}";
        }

        private static string DisplayName(ApplicationUser user)
            => string.IsNullOrWhiteSpace(user.FullName) ? user.Email ?? "there" : user.FullName.Trim();

        private static string FormatTerminationReason(TenancyTerminationReasonEnum? reason, bool french)
            => reason switch
            {
                TenancyTerminationReasonEnum.TenantMovedOut => french ? "départ du locataire" : "tenant moved out",
                TenancyTerminationReasonEnum.Eviction => french ? "procédure d’expulsion" : "eviction",
                TenancyTerminationReasonEnum.AgreementEnded => french ? "fin de l’accord" : "agreement ended",
                TenancyTerminationReasonEnum.LandlordDecision => french ? "décision du bailleur" : "landlord decision",
                TenancyTerminationReasonEnum.TenantRequest => french ? "demande du locataire" : "tenant request",
                _ => french ? "autre" : "other"
            };

        private static string FormatDate(DateTimeOffset value, PlatformLanguage language)
            => value.ToString("d", CultureInfo.GetCultureInfo(language.ToCultureName()));
    }
}
