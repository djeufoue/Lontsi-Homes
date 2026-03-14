using System;
using System.Collections.Generic;

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
        public int RentReminderDaysBeforeDue { get; set; }
        public int LeaseTerminationReminderDaysBeforeEnd { get; set; }
        public bool CanWrite { get; set; }
    }
}
