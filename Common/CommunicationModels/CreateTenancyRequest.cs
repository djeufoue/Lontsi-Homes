using System;
using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    public class CreateTenancyRequest
    {
        [Required]
        public int ApartmentId { get; set; }

        [Required]
        public DateTimeOffset StartDate { get; set; }

        public DateTimeOffset? EndDate { get; set; }

        [Required]
        public decimal MonthlyRent { get; set; }

        public int MaxMembers { get; set; } = 1;
    }
}
