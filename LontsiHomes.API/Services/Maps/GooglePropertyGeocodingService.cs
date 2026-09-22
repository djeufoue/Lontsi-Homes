using System.Text.Json;

namespace LontsiHomes.API.Services.Maps;

public sealed class GooglePropertyGeocodingService : IPropertyGeocodingService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GooglePropertyGeocodingService> _logger;

    public GooglePropertyGeocodingService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GooglePropertyGeocodingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<PropertyCoordinates?> GeocodeAsync(
        string address,
        string city,
        string? countryIsoCode,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _configuration["Maps:GoogleGeocodingApiKey"]?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Google Geocoding API key is not configured; address geocoding was skipped.");
            return null;
        }

        var fullAddress = string.Join(", ", new[] { address, city, countryIsoCode }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var url = "https://maps.googleapis.com/maps/api/geocode/json" +
                  $"?address={Uri.EscapeDataString(fullAddress)}&key={Uri.EscapeDataString(apiKey)}";

        try
        {
            using var response = await _httpClientFactory.CreateClient().GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Google geocoding returned HTTP {StatusCode}.", response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var status = json.RootElement.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;
            if (!string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase) ||
                !json.RootElement.TryGetProperty("results", out var results) ||
                results.GetArrayLength() == 0)
            {
                _logger.LogWarning("Google geocoding did not resolve {Address}. Status: {Status}.", fullAddress, status);
                return null;
            }

            var first = results[0];
            var location = first.GetProperty("geometry").GetProperty("location");
            var latitude = location.GetProperty("lat").GetDouble();
            var longitude = location.GetProperty("lng").GetDouble();
            var formattedAddress = first.TryGetProperty("formatted_address", out var formatted)
                ? formatted.GetString() ?? fullAddress
                : fullAddress;
            return new PropertyCoordinates(latitude, longitude, formattedAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Google geocoding failed for {Address}.", fullAddress);
            return null;
        }
    }
}
