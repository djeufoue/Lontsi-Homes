using Common.CommunicationModels;

namespace LontsiHomes.Portal.ViewModels.Documents
{
    public class DocumentsIndexVm
    {
        public string? Search { get; set; }
        public List<DocumentDto> Items { get; set; } = new();
    }

}
