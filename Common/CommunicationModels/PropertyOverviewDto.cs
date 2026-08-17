using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Common.CommunicationModels
{
    public class PropertyOverviewDto
    {
        public PropertyDetailDto Property { get; set; } = new();
        public List<PropertyManagerDto> Managers { get; set; } = new();
        public List<DocumentDto> Documents { get; set; } = new();

        // utile pour le MVC pour afficher/masquer les actions (AddManager, upload doc, etc.)
        public bool CanWrite { get; set; }
        public bool CanManageManagers { get; set; }
        public bool CanAddApartment { get; set; }
        public bool CanUploadDocuments { get; set; }
        public bool CanDeleteDocuments { get; set; }
    }
}
