using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
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

            var serviceClient = new BlobServiceClient(_connectionString);
            _containerClient = serviceClient.GetBlobContainerClient(_containerName);
            await _containerClient.CreateIfNotExistsAsync();
            return _containerClient;
        }
    }
}
