using Common.Enums;
using Microsoft.AspNetCore.Http;
using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{

    public class UploadApartmentDocumentRequest
    {
        [Required]
        public IFormFile File { get; set; } = default!;

        [Required]
        public DocumentTypeEnum DocumentType { get; set; }
    }
}
