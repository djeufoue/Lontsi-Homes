using Common.Enums;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Common.CommunicationModels
{
    public class UploadTenancyDocumentRequest
    {
        [Required]
        public IFormFile File { get; set; } = default!;

        [Required]
        public DocumentTypeEnum DocumentType { get; set; }
    }
}
