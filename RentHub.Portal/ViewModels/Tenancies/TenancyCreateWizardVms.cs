using System.ComponentModel.DataAnnotations;
using Common.CommunicationModels;
using Common.Enums;
using Microsoft.AspNetCore.Http;

namespace RentHub.Portal.ViewModels.Tenancies
{
    public class TenancyCreateDraft
    {
        public int ApartmentId { get; set; }
        public string ApartmentName { get; set; } = string.Empty;
        public int PropertyId { get; set; }
        public string PropertyName { get; set; } = string.Empty;
        public DateTimeOffset StartDate { get; set; }
        public DateTimeOffset? EndDate { get; set; }
        public decimal MonthlyRent { get; set; }
        public int MaxMembers { get; set; } = 1;
        public int RentDueDay { get; set; } = 1;
        public TenancyEndBehaviorEnum EndBehavior { get; set; } = TenancyEndBehaviorEnum.NoEndDate;
        public RentPaymentImportModeEnum ImportMode { get; set; } = RentPaymentImportModeEnum.AllGeneratedPeriodsUnpaid;
        public DateTimeOffset? UnpaidFrom { get; set; }
        public DateTimeOffset? UnpaidTo { get; set; }
        public DateTimeOffset? PaidInAdvanceFrom { get; set; }
        public DateTimeOffset? PaidInAdvanceTo { get; set; }
        public string? ContractTempPath { get; set; }
        public string? ContractFileName { get; set; }
        public string? ContractContentType { get; set; }
        public TenantInvitationRequest MainTenant { get; set; } = new();
        public List<RentPeriodSeedDto> RentPeriods { get; set; } = new();
    }

    public class TenancyCreateWizardVm
    {
        public int Step { get; set; } = 1;
        public TenancyCreateDraft Draft { get; set; } = new();
        public List<string> ValidationErrors { get; set; } = new();
        public bool CanContinue => ValidationErrors.Count == 0;
    }

    public class TenancyDetailsStepVm
    {
        public int ApartmentId { get; set; }

        [Required]
        [DataType(DataType.Date)]
        public DateTimeOffset StartDate { get; set; } = DateTimeOffset.UtcNow.Date;

        [DataType(DataType.Date)]
        public DateTimeOffset? EndDate { get; set; }

        [Required]
        [Range(0.01, double.MaxValue)]
        public decimal MonthlyRent { get; set; }

        [Range(1, int.MaxValue)]
        public int MaxMembers { get; set; } = 1;

        [Range(1, 31)]
        public int RentDueDay { get; set; } = 1;

        public TenancyEndBehaviorEnum EndBehavior { get; set; } = TenancyEndBehaviorEnum.NoEndDate;

        public IFormFile? ContractDocument { get; set; }
    }

    public class TenancyImportStepVm
    {
        public int ApartmentId { get; set; }
        public RentPaymentImportModeEnum ImportMode { get; set; } = RentPaymentImportModeEnum.AllGeneratedPeriodsUnpaid;

        [DataType(DataType.Date)]
        public DateTimeOffset? UnpaidFrom { get; set; }

        [DataType(DataType.Date)]
        public DateTimeOffset? UnpaidTo { get; set; }

        [DataType(DataType.Date)]
        public DateTimeOffset? PaidInAdvanceFrom { get; set; }

        [DataType(DataType.Date)]
        public DateTimeOffset? PaidInAdvanceTo { get; set; }
    }

    public class TenancyTenantStepVm
    {
        public int ApartmentId { get; set; }

        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        public string? FullName { get; set; }

        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Country code must contain only digits and may start with +.")]
        public string? CountryCode { get; set; }

        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Phone number must contain only digits and may start with +.")]
        public string? PhoneNumber { get; set; }

        [RegularExpression(@"^\+?\d+$", ErrorMessage = "WhatsApp number must contain only digits and may start with +.")]
        public string? WhatsAppPhoneNumber { get; set; }

        public TenancyMemberRoleEnum Role { get; set; } = TenancyMemberRoleEnum.MainTenant;
    }
}
