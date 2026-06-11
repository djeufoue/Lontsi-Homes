using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Common.Enums;

namespace Common.CommunicationModels
{
    public class TenancyOverviewDto
    {
        public TenancyDetailsDto Tenancy { get; set; } = new();

        public List<TenancyMemberDto> Members { get; set; } = new();

        public List<DocumentDto> Documents { get; set; } = new();

        public List<RentPeriodDto> RentPeriods { get; set; } = new();
    }

    public class TenancyDetailsDto
    {
        public int Id { get; set; }

        public int ApartmentId { get; set; }
        public string ApartmentName { get; set; } = string.Empty;

        public int PropertyId { get; set; }
        public string PropertyName { get; set; } = string.Empty;

        public DateTimeOffset StartDate { get; set; }
        public DateTimeOffset? EndDate { get; set; }

        public decimal MonthlyRent { get; set; }
        public int MaxMembers { get; set; }
        public int RentDueDay { get; set; } = 1;
        public TenancyEndBehaviorEnum EndBehavior { get; set; } = TenancyEndBehaviorEnum.NoEndDate;
        public DateTimeOffset? TerminatedAt { get; set; }
        public TenancyTerminationReasonEnum? TerminationReason { get; set; }
        public string? TerminationNotes { get; set; }
        public string Status { get; set; } = string.Empty;

        public bool CanWrite { get; set; }
    }
}
