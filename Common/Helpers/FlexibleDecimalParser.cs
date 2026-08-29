using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Common.Helpers
{
    public static class FlexibleDecimalParser
    {
        public static bool TryParse(string? input, out decimal value)
        {
            value = default;
            return TryNormalize(input, out var normalized) &&
                   decimal.TryParse(
                       normalized,
                       NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                       CultureInfo.InvariantCulture,
                       out value);
        }

        public static bool TryNormalize(string? input, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var compactBuilder = new StringBuilder(input.Length);
            foreach (var character in input.Trim())
            {
                if (char.IsWhiteSpace(character) || character is '\'' or '’')
                {
                    continue;
                }

                compactBuilder.Append(character);
            }

            var compact = compactBuilder.ToString();
            var sign = string.Empty;
            if (compact.StartsWith('+') || compact.StartsWith('-'))
            {
                sign = compact[..1];
                compact = compact[1..];
            }

            if (compact.Length == 0 || compact.Any(character => !char.IsDigit(character) && character is not '.' and not ','))
            {
                return false;
            }

            var dotIndexes = SeparatorIndexes(compact, '.');
            var commaIndexes = SeparatorIndexes(compact, ',');
            int? decimalIndex = null;

            if (dotIndexes.Count > 0 && commaIndexes.Count > 0)
            {
                decimalIndex = Math.Max(dotIndexes[^1], commaIndexes[^1]);
            }
            else
            {
                var indexes = dotIndexes.Count > 0 ? dotIndexes : commaIndexes;
                if (indexes.Count > 0)
                {
                    var digitsAfterLastSeparator = compact.Length - indexes[^1] - 1;
                    if (digitsAfterLastSeparator is 1 or 2)
                    {
                        decimalIndex = indexes[^1];
                    }
                    else if (digitsAfterLastSeparator == 0)
                    {
                        return false;
                    }
                }
            }

            var integerPart = decimalIndex.HasValue ? compact[..decimalIndex.Value] : compact;
            var fractionalPart = decimalIndex.HasValue ? compact[(decimalIndex.Value + 1)..] : string.Empty;
            integerPart = RemoveSeparators(integerPart);
            fractionalPart = RemoveSeparators(fractionalPart);

            if ((integerPart.Length > 0 && integerPart.Any(character => !char.IsDigit(character))) ||
                (fractionalPart.Length > 0 && fractionalPart.Any(character => !char.IsDigit(character))) ||
                (integerPart.Length == 0 && fractionalPart.Length == 0))
            {
                return false;
            }

            integerPart = integerPart.Length == 0 ? "0" : integerPart;
            normalized = fractionalPart.Length == 0
                ? sign + integerPart
                : sign + integerPart + "." + fractionalPart;
            return true;
        }

        private static List<int> SeparatorIndexes(string value, char separator)
        {
            var indexes = new List<int>();
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] == separator)
                {
                    indexes.Add(index);
                }
            }

            return indexes;
        }

        private static string RemoveSeparators(string value)
            => value.Replace(".", string.Empty, StringComparison.Ordinal)
                .Replace(",", string.Empty, StringComparison.Ordinal);
    }
}
