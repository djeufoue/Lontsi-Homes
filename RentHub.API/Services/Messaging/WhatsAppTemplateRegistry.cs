using Microsoft.Extensions.Options;
using RentHub.API.Models.Settings;

namespace RentHub.API.Services.Messaging;

public sealed record WhatsAppTemplateDefinition(
    string EventType,
    string TemplateName,
    string LanguageCode,
    string Category,
    int BodyPlaceholderCount,
    int UrlButtonParameterCount,
    IReadOnlySet<string> RecipientRoles,
    string? ProviderTemplateId = null,
    bool RequiresDocument = false);

public interface IWhatsAppTemplateRegistry
{
    bool TryGet(string eventType, string languageCode, out WhatsAppTemplateDefinition definition);
    bool TryGetByTemplate(string templateName, string languageCode, out WhatsAppTemplateDefinition definition);
    bool IsApproved(WhatsAppTemplateDefinition definition);
    IReadOnlyCollection<WhatsAppTemplateDefinition> All { get; }
}

public sealed class WhatsAppTemplateRegistry : IWhatsAppTemplateRegistry
{
    private readonly InfobipOptions _options;
    private readonly IReadOnlyCollection<WhatsAppTemplateDefinition> _definitions;

    public WhatsAppTemplateRegistry(IOptions<InfobipOptions> options)
    {
        _options = options.Value;
        var tenant = Roles("Tenant");
        var landlord = Roles("Landlord", "Manager", "Admin");
        var account = Roles("Landlord", "Tenant", "Owner", "Manager", "Admin", "Visitor");

        _definitions = new[]
        {
            Define("whatsapp_verification", "whatsapp_verification_code_v1", "fr", "Authentication", 1, 1, account),
            Define("whatsapp_verification", "whatsapp_verification_code_v1", "en_GB", "Authentication", 1, 1, account),
            Define("rent_due_soon", "rent_due_soon", "fr", "Utility", 6, 1, tenant),
            Define("rent_due_soon", "rent_due_soon", "en", "Utility", 6, 1, tenant),
            Define("rent_due_today", "rent_due_today", "fr", "Utility", 6, 1, tenant),
            Define("rent_due_today", "rent_due_today", "en", "Utility", 6, 1, tenant),
            Define("rent_overdue", "rent_overdue", "fr", "Utility", 6, 1, tenant),
            Define("rent_overdue", "rent_overdue", "en", "Utility", 6, 1, tenant),
            Define("tenant_rent_payment_received", "rent_payment_receipt", "fr", "Utility", 7, 0, tenant, requiresDocument: true),
            Define("tenant_rent_payment_received", "rent_payment_receipt", "en", "Utility", 7, 0, tenant, requiresDocument: true),
            Define("tenancy_ending_soon", "tenancy_ending_soon", "fr", "Utility", 4, 1, tenant),
            Define("tenancy_ending_soon", "tenancy_ending_soon", "en", "Utility", 4, 1, tenant),
            Define("tenancy_request_received", "tenancy_request_received", "fr", "Utility", 4, 0, landlord),
            Define("tenancy_request_received", "tenancy_request_received", "en", "Utility", 4, 0, landlord),
            Define("tenancy_request_status_update", "tenancy_request_status_update", "fr", "Utility", 5, 0, tenant),
            Define("tenancy_request_status_update", "tenancy_request_status_update", "en", "Utility", 5, 0, tenant),
            Define("landlord_rent_payment_received", "landlord_rent_payment_received", "fr", "Utility", 5, 1, landlord),
            Define("landlord_rent_payment_received", "landlord_rent_payment_received", "en", "Utility", 5, 1, landlord)
        };
    }

    public IReadOnlyCollection<WhatsAppTemplateDefinition> All => _definitions;

    public bool TryGet(string eventType, string languageCode, out WhatsAppTemplateDefinition definition)
    {
        definition = _definitions.FirstOrDefault(item =>
            item.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase) &&
            LanguageMatches(item.LanguageCode, languageCode))!;
        return definition is not null;
    }

    public bool TryGetByTemplate(string templateName, string languageCode, out WhatsAppTemplateDefinition definition)
    {
        definition = _definitions.FirstOrDefault(item =>
            item.TemplateName.Equals(templateName, StringComparison.OrdinalIgnoreCase) &&
            LanguageMatches(item.LanguageCode, languageCode))!;
        return definition is not null;
    }

    public bool IsApproved(WhatsAppTemplateDefinition definition)
    {
        return _options.Templates.TryGetValue(Key(definition.TemplateName, definition.LanguageCode), out var configured) &&
               configured.Approved;
    }

    private WhatsAppTemplateDefinition Define(
        string eventType,
        string templateName,
        string languageCode,
        string category,
        int bodyPlaceholderCount,
        int urlButtonParameterCount,
        IReadOnlySet<string> roles,
        bool requiresDocument = false)
    {
        _options.Templates.TryGetValue(Key(templateName, languageCode), out var configured);
        return new WhatsAppTemplateDefinition(
            eventType,
            templateName,
            languageCode,
            category,
            bodyPlaceholderCount,
            urlButtonParameterCount,
            roles,
            configured?.ProviderTemplateId,
            requiresDocument);
    }

    private static HashSet<string> Roles(params string[] roles) =>
        new(roles, StringComparer.OrdinalIgnoreCase);

    private static string Key(string templateName, string languageCode) => $"{templateName}:{languageCode}";

    private static bool LanguageMatches(string configured, string requested) =>
        configured.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
        (configured.StartsWith("en", StringComparison.OrdinalIgnoreCase) && requested.StartsWith("en", StringComparison.OrdinalIgnoreCase));
}
