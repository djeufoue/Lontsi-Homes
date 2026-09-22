namespace LontsiHomes.API.Models.Settings
{
    /// <summary>
    /// Represents JWT configuration parameters loaded from appsettings.json.  The issuer,
    /// audience and signing key are required to generate and validate tokens.
    /// </summary>
    public class JwtSettings
    {
        public string Issuer { get; set; } = string.Empty;
        public string Audience { get; set; } = string.Empty;
        public string SigningKey { get; set; } = string.Empty;
        public int ExpiryMinutes { get; set; } = 60;
    }
}