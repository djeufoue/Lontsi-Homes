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
        private readonly BlobContainerClient _containerClient;

        public AzureStorageService(IConfiguration configuration)
        {
            var connectionString = configuration.GetSection("AzureStorage:ConnectionString").Value
                ?? throw new InvalidOperationException("Azure storage connection string is missing.");
            var containerName = configuration.GetSection("AzureStorage:ContainerName").Value
                ?? "renthub-files";
            var serviceClient = new BlobServiceClient(connectionString);
            _containerClient = serviceClient.GetBlobContainerClient(containerName);
            _containerClient.CreateIfNotExists();
        }

        public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType)
        {
            var blobClient = _containerClient.GetBlobClient(fileName);
            await blobClient.UploadAsync(fileStream, new BlobHttpHeaders { ContentType = contentType });
            return blobClient.Uri.ToString();
        }

        public async Task DeleteFileAsync(string fileUrl)
        {
            var blobClient = new BlobClient(new Uri(fileUrl));
            await blobClient.DeleteIfExistsAsync();
        }
    }
}