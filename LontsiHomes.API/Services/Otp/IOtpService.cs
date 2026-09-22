namespace LontsiHomes.API.Services.Otp;

public interface IOtpService
{
    string GenerateCode(int digits = 6);
    string HashCode(string code, string purpose, string userId);
    bool VerifyCode(string code, string expectedHash, string purpose, string userId);
}
