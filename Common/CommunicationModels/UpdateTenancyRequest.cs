using System;
using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    public class UpdateTenancyRequest
    {
        [Required]
        public DateTimeOffset StartDate { get; set; }

        public DateTimeOffset? EndDate { get; set; }

        [Required]
        [Range(0, double.MaxValue)]
        public decimal MonthlyRent { get; set; }

        [Range(1, int.MaxValue)]
        public int MaxMembers { get; set; } = 1;
    }
}
