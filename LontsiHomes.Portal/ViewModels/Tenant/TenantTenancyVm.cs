using Common.CommunicationModels;

namespace LontsiHomes.Portal.ViewModels.Tenant;

public class TenantTenancyVm
{
    public TenantDashboardTenancyDto Item { get; set; } = new();

    public List<RentPeriodDto> RentPeriods { get; set; } = new();
    public int PeriodPage { get; set; } = 1;
    public int PeriodPageSize { get; set; } = 10;
    public int TotalRentPeriods { get; set; }
    public int TotalPeriodPages { get; set; } = 1;

    public List<TenantPaymentHistoryDto> PaymentHistory { get; set; } = new();
    public int HistoryPage { get; set; } = 1;
    public int HistoryPageSize { get; set; } = 10;
    public int TotalPayments { get; set; }
    public int TotalHistoryPages { get; set; } = 1;
}
