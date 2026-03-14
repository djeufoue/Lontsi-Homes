using Common.CommunicationModels;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Storage;

namespace RentHub.API.Helpers
{
    public static class DocumentHelpers
    {
        public static async Task<DocumentDto> ToDtoAsync(Document document, IStorageService storageService)
        {
            return new DocumentDto
            {
                Id = document.Id,
                FileName = document.FileName,
                BlobUrl = await storageService.GetReadUrlAsync(document.BlobUrl),
                DocumentType = document.DocumentType,
                UploadedAt = document.UploadedAt,
                PropertyId = document.PropertyId,
                ApartmentId = document.ApartmentId,
                TenancyId = document.TenancyId
            };
        }

        public static async Task<List<DocumentDto>> ToDtosAsync(IEnumerable<Document> documents, IStorageService storageService)
        {
            var list = new List<DocumentDto>();
            foreach (var document in documents)
            {
                list.Add(await ToDtoAsync(document, storageService));
            }

            return list;
        }
    }
}
