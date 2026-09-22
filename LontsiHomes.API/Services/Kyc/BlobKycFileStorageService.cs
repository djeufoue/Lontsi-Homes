using Microsoft.AspNetCore.Http;
using LontsiHomes.API.Services.Storage;

namespace LontsiHomes.API.Services.Kyc
{
    public class BlobKycFileStorageService : IKycFileStorageService
    {
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg",
            ".jpeg",
            ".png",
            ".webp",
            ".pdf"
        };

        private readonly IStorageService _storageService;
        private readonly ILogger<BlobKycFileStorageService> _logger;

        public BlobKycFileStorageService(IStorageService storageService, ILogger<BlobKycFileStorageService> logger)
        {
            _storageService = storageService;
            _logger = logger;
        }

        public async Task<KycStoredFile> SaveAsync(string userId, string category, IFormFile file, CancellationToken cancellationToken = default)
        {
            if (file.Length <= 0)
            {
                throw new InvalidOperationException("The uploaded file is empty.");
            }

            if (file.Length > 8 * 1024 * 1024)
            {
                throw new InvalidOperationException("KYC files must be 8 MB or smaller.");
            }

            var extension = Path.GetExtension(file.FileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(extension) || !AllowedExtensions.Contains(extension))
            {
                throw new InvalidOperationException("Upload JPG, PNG, WEBP, or PDF files only.");
            }

            var safeUserId = SanitizeSegment(userId);
            var safeCategory = SanitizeSegment(category);
            var blobName = $"kyc/{safeUserId}/{safeCategory}-{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
            var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? GuessContentType(file.FileName) : file.ContentType;

            await using var stream = file.OpenReadStream();
            var fileUrl = await _storageService.UploadFileAsync(stream, blobName, contentType);

            return new KycStoredFile(
                fileUrl,
                contentType,
                Path.GetFileName(file.FileName ?? blobName));
        }

        public Task<string> GetReadUrlAsync(string storedPath, TimeSpan? lifetime = null, CancellationToken cancellationToken = default)
        {
            return _storageService.GetReadUrlAsync(storedPath, lifetime);
        }

        public async Task DeleteFilesAsync(IEnumerable<string> storedPaths, CancellationToken cancellationToken = default)
        {
            foreach (var storedPath in storedPaths.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct())
            {
                try
                {
                    await _storageService.DeleteFileAsync(storedPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete KYC file {StoredPath}.", storedPath);
                }
            }
        }

        public Task DeleteUserFilesAsync(string userId, CancellationToken cancellationToken = default)
        {
            // The generic storage abstraction does not expose prefix listing.
            // Call DeleteFilesAsync with the profile's stored URLs when deleting a known KYC profile.
            return Task.CompletedTask;
        }

        private static string SanitizeSegment(string value)
        {
            var chars = value
                .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
                .ToArray();

            return chars.Length == 0 ? "unknown" : new string(chars);
        }

        private static string GuessContentType(string? fileName)
        {
            return Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".pdf" => "application/pdf",
                _ => "application/octet-stream"
            };
        }
    }
}
