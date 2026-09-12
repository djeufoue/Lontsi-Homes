using System.Security.Cryptography;
using System.Text;

namespace RentHub.API.Services.Otp;

public sealed class CryptographicOtpService : IOtpService
{
    private readonly byte[] _key;

    public CryptographicOtpService(IConfiguration configuration)
    {
        var configuredKey = configuration["Otp:HashKey"];
        _key = string.IsNullOrWhiteSpace(configuredKey)
            ? SHA256.HashData(Encoding.UTF8.GetBytes("RentHub-development-only-otp-key"))
            : SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
    }

    public string GenerateCode(int digits = 6)
    {
        if (digits is < 4 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(digits));
        }

        var upperBound = (int)Math.Pow(10, digits);
        return RandomNumberGenerator.GetInt32(0, upperBound).ToString($"D{digits}");
    }

    public string HashCode(string code, string purpose, string userId)
    {
        var payload = Encoding.UTF8.GetBytes($"{purpose}\n{userId}\n{code}");
        return Convert.ToHexString(HMACSHA256.HashData(_key, payload));
    }

    public bool VerifyCode(string code, string expectedHash, string purpose, string userId)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        var actual = Encoding.UTF8.GetBytes(HashCode(code, purpose, userId));
        var expected = Encoding.UTF8.GetBytes(expectedHash.Trim());
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
