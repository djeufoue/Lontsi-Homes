using System;
using System.ComponentModel.DataAnnotations;
using Common.Enums;

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

        [Range(1, 31)]
        public int RentDueDay { get; set; } = 1;

        [Range(1, 12)]
        public int PaymentIntervalMonths { get; set; } = 1;

        public TenancyEndBehaviorEnum EndBehavior { get; set; } = TenancyEndBehaviorEnum.NoEndDate;

        public DateTimeOffset? RentTrackingStartDate { get; set; }
    }
}
