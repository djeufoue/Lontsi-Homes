using Common.CommunicationModels;

namespace LontsiHomes.Portal.ViewModels.Documents
{
    public class DocumentPreviewVm
    {
        public DocumentDto Document { get; set; } = new();
        public int? PropertyId { get; set; }
        public string PropertyName { get; set; } = string.Empty;
        public int? ApartmentId { get; set; }
        public string? ApartmentName { get; set; }
        public int? TenancyId { get; set; }
        public string? TenancyName { get; set; }
        public string SectionLabel { get; set; } = string.Empty;
        public bool IsImage { get; set; }
        public bool IsPdf { get; set; }
    }
}
