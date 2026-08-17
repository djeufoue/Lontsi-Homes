using System.Globalization;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Email;

namespace RentHub.API.Services.Tenancies
{
    public interface ITenancyRenewalEmailService
    {
        Task SendExpiryReminderAsync(ApplicationUser tenant, Tenancy tenancy, CancellationToken cancellationToken = default);
        Task SendRequestSubmittedAsync(TenancyExtensionRequest request, CancellationToken cancellationToken = default);
        Task SendDecisionAsync(TenancyExtensionRequest request, CancellationToken cancellationToken = default);
    }

    public class TenancyRenewalEmailService : ITenancyRenewalEmailService
    {
        private readonly ApplicationDbContext _context;
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;

        public TenancyRenewalEmailService(
            ApplicationDbContext context,
            IEmailService emailService,
            IConfiguration configuration)
        {
            _context = context;
            _emailService = emailService;
            _configuration = configuration;
        }

        public async Task SendExpiryReminderAsync(
            ApplicationUser tenant,
            Tenancy tenancy,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(tenant.Email) || !tenancy.EndDate.HasValue)
            {
                return;
            }

            var propertyName = tenancy.Apartment?.Property?.Name ?? string.Empty;
            var apartmentName = tenancy.Apartment?.Name ?? string.Empty;
            var renewalUrl = BuildPortalUrl($"/Tenancies/Renewal?tenancyId={tenancy.Id}");
            var endDate = FormatDate(tenancy.EndDate.Value, tenant.Language);
            var name = DisplayName(tenant);

            var subject = tenant.Language == PlatformLanguage.French
                ? $"Votre bail arrive bientôt à échéance – {apartmentName}"
                : $"Your tenancy is ending soon - {apartmentName}";
            var body = tenant.Language == PlatformLanguage.French
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

            await _emailService.SendEmailAsync(tenant.Email, subject, body);
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
                .Where(user => recipientIds.Contains(user.Id) && user.Email != null && user.Email != string.Empty)
                .ToListAsync(cancellationToken);
            var reviewUrl = BuildPortalUrl($"/Tenancies/Renewal?tenancyId={tenancy.Id}");
            var requesterName = request.RequestedBy == null ? "Tenant" : DisplayName(request.RequestedBy);

            foreach (var recipient in recipients.DistinctBy(user => user.Email, StringComparer.OrdinalIgnoreCase))
            {
                var proposedDate = FormatDate(request.ProposedEndDate, recipient.Language);
                var subject = recipient.Language == PlatformLanguage.French
                    ? $"Nouvelle demande de renouvellement – {tenancy.Apartment.Name}"
                    : $"New tenancy renewal request - {tenancy.Apartment.Name}";
                var body = recipient.Language == PlatformLanguage.French
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

                await _emailService.SendEmailAsync(recipient.Email!, subject, body);
            }
        }

        public async Task SendDecisionAsync(
            TenancyExtensionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.RequestedBy == null || string.IsNullOrWhiteSpace(request.RequestedBy.Email) || request.Tenancy?.Apartment == null)
            {
                return;
            }

            var tenant = request.RequestedBy;
            var approved = request.Status == TenancyExtensionStatusEnum.Approved;
            var renewalUrl = BuildPortalUrl($"/Tenancies/Renewal?tenancyId={request.TenancyId}");
            var proposedDate = FormatDate(request.ProposedEndDate, tenant.Language);
            var subject = tenant.Language == PlatformLanguage.French
                ? approved ? "Votre renouvellement a été approuvé" : "Votre renouvellement a été refusé"
                : approved ? "Your tenancy renewal was approved" : "Your tenancy renewal was rejected";

            var lines = new List<string>
            {
                tenant.Language == PlatformLanguage.French
                    ? $"Bonjour {DisplayName(tenant)},"
                    : $"Hello {DisplayName(tenant)},",
                string.Empty
            };

            if (tenant.Language == PlatformLanguage.French)
            {
                lines.Add(approved
                    ? $"Votre demande de renouvellement pour {request.Tenancy.Apartment.Name} a été approuvée. La nouvelle date de fin est le {proposedDate}."
                    : $"Votre demande de renouvellement pour {request.Tenancy.Apartment.Name} a été refusée.");
                if (!approved && !string.IsNullOrWhiteSpace(request.RejectionReason))
                {
                    lines.Add($"Raison : {request.RejectionReason}");
                }
                lines.Add($"Consulter la demande : {renewalUrl}");
            }
            else
            {
                lines.Add(approved
                    ? $"Your renewal request for {request.Tenancy.Apartment.Name} was approved. The new end date is {proposedDate}."
                    : $"Your renewal request for {request.Tenancy.Apartment.Name} was rejected.");
                if (!approved && !string.IsNullOrWhiteSpace(request.RejectionReason))
                {
                    lines.Add($"Reason: {request.RejectionReason}");
                }
                lines.Add($"View the request: {renewalUrl}");
            }

            lines.Add(string.Empty);
            lines.Add("Lontsi Homes");
            await _emailService.SendEmailAsync(tenant.Email, subject, string.Join(Environment.NewLine, lines));
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
