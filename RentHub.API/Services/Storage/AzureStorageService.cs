using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;

namespace RentHub.API.Services.Storage
{
    /// <summary>
    /// Stores files in Azure Blob Storage.  Container name and connection string are loaded
    /// from configuration under the "AzureStorage" section.
    /// </summary>
    public class AzureStorageService : IStorageService
    {
        private readonly string? _connectionString;
        private readonly string _containerName;
        private BlobServiceClient? _serviceClient;
        private BlobContainerClient? _containerClient;

        public AzureStorageService(IConfiguration configuration)
        {
            _connectionString = configuration.GetSection("AzureStorage:ConnectionString").Value;
            _containerName = configuration.GetSection("AzureStorage:ContainerName").Value
                ?? "renthub-files";
        }

        public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType)
        {
            var containerClient = await GetContainerClientAsync();
            var blobClient = containerClient.GetBlobClient(fileName);
            await blobClient.UploadAsync(fileStream, new BlobHttpHeaders { ContentType = contentType });
            return blobClient.Uri.ToString();
        }

        public async Task<string> GetReadUrlAsync(string fileUrl, TimeSpan? lifetime = null)
        {
            if (string.IsNullOrWhiteSpace(fileUrl))
            {
                return string.Empty;
            }

            if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out var fileUri))
            {
                return fileUrl;
            }

            if (!string.IsNullOrWhiteSpace(fileUri.Query))
            {
                return fileUrl;
            }

            var containerClient = await GetContainerClientAsync();
            var uriBuilder = new BlobUriBuilder(fileUri);
            var blobClient = containerClient.GetBlobClient(uriBuilder.BlobName);

            if (!blobClient.CanGenerateSasUri)
            {
                return fileUrl;
            }

            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = containerClient.Name,
                BlobName = uriBuilder.BlobName,
                Resource = "b",
                ExpiresOn = DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromHours(4))
            };
            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            return blobClient.GenerateSasUri(sasBuilder).ToString();
        }

        public async Task DeleteFileAsync(string fileUrl)
        {
            await GetContainerClientAsync();
            var blobClient = new BlobClient(new Uri(fileUrl));
            await blobClient.DeleteIfExistsAsync();
        }

        private async Task<BlobContainerClient> GetContainerClientAsync()
        {
            if (_containerClient != null)
            {
                return _containerClient;
            }

            if (string.IsNullOrWhiteSpace(_connectionString))
            {
                throw new InvalidOperationException("File storage is not configured yet. Please ask an administrator to set up Azure Storage.");
            }

            _serviceClient ??= new BlobServiceClient(_connectionString);
            _containerClient = _serviceClient.GetBlobContainerClient(_containerName);
            await _containerClient.CreateIfNotExistsAsync();
            return _containerClient;
        }
    }
}
