using Microsoft.AspNetCore.Http;

namespace RentHub.API.Services.Kyc
{
    public record KycStoredFile(string RelativePath, string ContentType, string OriginalFileName);

    public interface IKycFileStorageService
    {
        Task<KycStoredFile> SaveAsync(string userId, string category, IFormFile file, CancellationToken cancellationToken = default);
        Task<string> GetReadUrlAsync(string storedPath, TimeSpan? lifetime = null, CancellationToken cancellationToken = default);
        Task DeleteFilesAsync(IEnumerable<string> storedPaths, CancellationToken cancellationToken = default);
        Task DeleteUserFilesAsync(string userId, CancellationToken cancellationToken = default);
    }
}
