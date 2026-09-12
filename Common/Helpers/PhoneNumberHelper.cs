using System.Linq;
using System.Text.RegularExpressions;

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

        /// <summary>
        /// Converts a country code and a local/international number to E.164.
        /// The method deliberately accepts only digits, spaces and common visual
        /// separators; extensions and ambiguous national formats are rejected.
        /// </summary>
        public static bool TryNormalizeE164(string? countryCode, string? phoneNumber, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(phoneNumber))
            {
                return false;
            }

            var rawNumber = phoneNumber.Trim();
            if (!Regex.IsMatch(rawNumber, @"^\+?[0-9\s().-]+$"))
            {
                return false;
            }

            string digits;
            if (rawNumber.StartsWith('+'))
            {
                digits = new string(rawNumber.Skip(1).Where(char.IsDigit).ToArray());
            }
            else
            {
                var countryDigits = new string((countryCode ?? string.Empty).Where(char.IsDigit).ToArray());
                if (countryDigits.Length == 0)
                {
                    return false;
                }

                var nationalDigits = new string(rawNumber.Where(char.IsDigit).ToArray()).TrimStart('0');
                digits = countryDigits + nationalDigits;
            }

            if (digits.Length is < 8 or > 15 || digits[0] == '0')
            {
                return false;
            }

            normalized = "+" + digits;
            return true;
        }

        public static string ToProviderDigits(string e164)
        {
            return new string((e164 ?? string.Empty).Where(char.IsDigit).ToArray());
        }
    }
}
