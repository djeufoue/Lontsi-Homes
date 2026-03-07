using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Common.CommunicationModels
{
    public class ApartmentOverviewDto
    {
        public ApartmentDetailsDto Apartment { get; set; } = new();
        public List<TenancyDto> Tenancies { get; set; } = new();
        public List<ApartmentOwnerDto> Owners { get; set; } = new();
        public List<DocumentDto> Documents { get; set; } = new();
    }

    public class ApartmentDetailsDto
    {
        public int Id { get; set; }
        public int PropertyId { get; set; }
        public string PropertyName { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public double? Area { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
