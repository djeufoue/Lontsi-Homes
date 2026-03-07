namespace RentHub.API.Services.Storage
{
    /// <summary>
    /// Abstraction over file storage.  This allows the API to store and retrieve
    /// documents without knowing the underlying provider.  Implementations can use
    /// Azure Blob Storage, AWS S3, local disk, etc.
    /// </summary>
    public interface IStorageService
    {
        /// <summary>
        /// Uploads a file stream and returns the URL to access it.
        /// </summary>
        Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType);

        /// <summary>
        /// Deletes a file at the specified URL.
        /// </summary>
        Task DeleteFileAsync(string fileUrl);
    }
}