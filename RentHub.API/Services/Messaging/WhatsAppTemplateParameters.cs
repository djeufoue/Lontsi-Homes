namespace RentHub.API.Services.Messaging;

public static class WhatsAppTemplateParameters
{
    public static bool AreValid(
        WhatsAppTemplateDefinition definition,
        IReadOnlyList<string> body,
        IReadOnlyList<string>? buttons,
        WhatsAppDocumentHeader? document)
    {
        if (body.Count != definition.BodyPlaceholderCount ||
            (buttons?.Count ?? 0) != definition.UrlButtonParameterCount ||
            definition.RequiresDocument != (document != null))
        {
            return false;
        }

        // Media must be served by a reachable HTTPS endpoint, not a local file/login page.
        // Reachability and access authorization belong to the receipt publishing service.
        return document == null ||
            (Uri.TryCreate(document.MediaUrl, UriKind.Absolute, out var uri) &&
             uri.Scheme == Uri.UriSchemeHttps && !uri.IsLoopback &&
             string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
             !string.IsNullOrWhiteSpace(document.Filename) &&
             document.Filename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) &&
             document.Filename.IndexOfAny(new[] { '/', '\\', '\r', '\n' }) < 0);
    }
}
