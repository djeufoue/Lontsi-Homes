namespace RentHub.API.Services.Maps;

public interface IPropertyGeocodingService
{
    Task<PropertyCoordinates?> GeocodeAsync(
        string address,
        string city,
        string? countryIsoCode,
        CancellationToken cancellationToken = default);
}

public sealed record PropertyCoordinates(double Latitude, double Longitude, string FormattedAddress);
