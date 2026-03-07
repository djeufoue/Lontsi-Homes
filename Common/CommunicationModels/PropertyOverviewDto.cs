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
    }
}
