using System.Linq;

namespace Common.Helpers
{
    /// <summary>
    /// Provides one normalization rule for every phone-like value accepted by the application.
    /// </summary>
    public static class PhoneNumberHelper
    {
        public const string DigitsWithOptionalLeadingPlusPattern = @"^\s*\+?(?:\s*\d)+\s*$";
        public const string CountryCodePattern = @"^\s*\+?(?:\s*\d){1,4}\s*$";

        /// <summary>
        /// Removes all Unicode whitespace while preserving the number and an optional leading +.
        /// Invalid non-whitespace characters are intentionally preserved so validation can reject them.
        /// </summary>
        public static string? Normalize(string? value)
        {
            return value == null
                ? null
                : new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray());
        }

        public static string NormalizeOrEmpty(string? value)
        {
            return Normalize(value) ?? string.Empty;
        }
    }
}
