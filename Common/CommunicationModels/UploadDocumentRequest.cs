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
    public class UploadDocumentRequest
    {
        [Required] public IFormFile File { get; set; } = default!;
        [Required] public DocumentTypeEnum Type { get; set; }
        public string? Caption { get; set; }
        // Optional FK hints if you need them here (but you already have route ids)
        public int? ApartmentId { get; set; }
        public int? TenancyId { get; set; }
    }
}
