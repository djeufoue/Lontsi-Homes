using Common.Enums;

namespace LontsiHomes.Portal.Helpers
{
    public static class ApartmentTypeDisplayHelper
    {
        public static string ResourceKey(ApartmentTypeEnum type) => type switch
        {
            ApartmentTypeEnum.Studio => "Studio",
            ApartmentTypeEnum.OneBedroom => "One bedroom",
            ApartmentTypeEnum.TwoBedroom => "Two bedrooms",
            ApartmentTypeEnum.ThreeBedroom => "Three bedrooms",
            ApartmentTypeEnum.Duplex => "Duplex",
            _ => type.ToString()
        };

        public static string ResourceKey(string? type)
        {
            return Enum.TryParse<ApartmentTypeEnum>(type, true, out var parsed)
                ? ResourceKey(parsed)
                : type ?? string.Empty;
        }
    }
}
