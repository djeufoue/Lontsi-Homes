using System;
using System.Linq;
using Common.Enums;

namespace Common.Helpers
{
    public static class CameroonMobileMoneyNumberHelper
    {
        public static bool TryNormalizeNationalNumber(string? phoneNumber, out string nationalNumber)
        {
            nationalNumber = string.Empty;
            if (string.IsNullOrWhiteSpace(phoneNumber))
            {
                return false;
            }

            var digits = new string(phoneNumber.Where(char.IsDigit).ToArray());
            if (digits.StartsWith("00", StringComparison.Ordinal))
            {
                digits = digits[2..];
            }

            if (digits.Length == 12 && digits.StartsWith("2376", StringComparison.Ordinal))
            {
                digits = digits[3..];
            }

            if (digits.Length != 9 || !digits.StartsWith("6", StringComparison.Ordinal))
            {
                return false;
            }

            nationalNumber = digits;
            return ResolveOperatorFromNationalNumber(digits).HasValue;
        }

        public static bool TryNormalizeInternationalNumber(string? phoneNumber, out string internationalNumber)
        {
            internationalNumber = string.Empty;
            if (!TryNormalizeNationalNumber(phoneNumber, out var nationalNumber))
            {
                return false;
            }

            internationalNumber = $"237{nationalNumber}";
            return true;
        }

        public static PayoutChannelEnum? ResolveOperator(string? phoneNumber)
        {
            return TryNormalizeNationalNumber(phoneNumber, out var nationalNumber)
                ? ResolveOperatorFromNationalNumber(nationalNumber)
                : null;
        }

        public static bool MatchesOperator(string? phoneNumber, PayoutChannelEnum? channel)
        {
            if (channel is not PayoutChannelEnum.MtnMoney and not PayoutChannelEnum.OrangeMoney)
            {
                return false;
            }

            return ResolveOperator(phoneNumber) == channel;
        }

        public static string ChannelLabel(PayoutChannelEnum? channel)
        {
            return channel switch
            {
                PayoutChannelEnum.MtnMoney => "MTN Mobile Money",
                PayoutChannelEnum.OrangeMoney => "Orange Money",
                _ => "Mobile Money"
            };
        }

        public static string SupportedPrefixesDescription =>
            "MTN Mobile Money: 67, 68, 650-654. Orange Money: 69, 655-659.";

        private static PayoutChannelEnum? ResolveOperatorFromNationalNumber(string nationalNumber)
        {
            if (nationalNumber.Length != 9)
            {
                return null;
            }

            var firstTwo = nationalNumber[..2];
            var firstThree = nationalNumber[..3];

            if (firstTwo is "67" or "68" ||
                IsPrefixInRange(firstThree, 650, 654))
            {
                return PayoutChannelEnum.MtnMoney;
            }

            if (firstTwo == "69" ||
                IsPrefixInRange(firstThree, 655, 659))
            {
                return PayoutChannelEnum.OrangeMoney;
            }

            return null;
        }

        private static bool IsPrefixInRange(string prefix, int min, int max)
        {
            return int.TryParse(prefix, out var value) && value >= min && value <= max;
        }
    }
}
