using Microsoft.Extensions.Configuration;

namespace RentHub.API.Models.Settings;

public sealed class InfobipOptions
{
    public const string SectionName = "Infobip";

    public string BaseUrl { get; set; } = "https://api.infobip.com";
    public string ApiKey { get; set; } = string.Empty;
    public string SmsSender { get; set; } = string.Empty;
    public string WhatsAppSender { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public Dictionary<string, InfobipTemplateOptions> Templates { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void BindTemplateConfiguration(IConfiguration configuration)
    {
        // Configuration treats ":" as a hierarchy separator, including inside JSON keys.
        // Rebuild the registry's template:language keys from the effective configuration
        // so JSON, user secrets and environment overrides all follow the same rules.
        var templates = new Dictionary<string, InfobipTemplateOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in configuration.GetSection($"{SectionName}:Templates").GetChildren())
        {
            foreach (var language in template.GetChildren())
            {
                templates[$"{template.Key}:{language.Key}"] = new InfobipTemplateOptions
                {
                    Approved = language.GetValue<bool>("Approved"),
                    ProviderTemplateId = language["ProviderTemplateId"] ?? string.Empty
                };
            }
        }

        Templates = templates;
    }
}

public sealed class InfobipTemplateOptions
{
    public string ProviderTemplateId { get; set; } = string.Empty;
    public bool Approved { get; set; }
}
