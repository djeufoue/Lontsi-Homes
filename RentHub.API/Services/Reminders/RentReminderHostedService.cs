using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Common.Helpers;
using RentHub.API.Data;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Email;
using RentHub.API.Services.Sms;
using Common.Enums;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RentHub.API.Services.Reminders
{
    /// <summary>
    /// Background service that periodically checks for upcoming rent payments and unpaid
    /// rents and sends email/SMS reminders.  The reminder timing is configured via
    /// <see cref="ReminderSettings"/> entities in the database.  This service
    /// runs at a fixed interval and should not block application startup.
    /// </summary>
    public class RentReminderHostedService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<RentReminderHostedService> _logger;
        private readonly IConfiguration _configuration;
        private readonly TimeSpan _interval;

        public RentReminderHostedService(
            IServiceProvider serviceProvider,
            ILogger<RentReminderHostedService> logger,
            IConfiguration configuration)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _configuration = configuration;
            // Run once a day by default.  In production, this could be configured.
            _interval = TimeSpan.FromHours(24);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
                        var smsService = scope.ServiceProvider.GetRequiredService<ISmsService>();
                        var nowUtc = DateTimeOffset.UtcNow;
                        // Find active tenancies (not ended and not deleted)
                        var tenancies = db.Tenancies
                            .Include(t => t.Members)
                            .ThenInclude(m => m.Member)
                            .Include(t => t.Apartment)
                            .ThenInclude(a => a!.Property)
                            .Include(t => t.RentPeriods)
                            .Where(t => !t.IsDeleted
                                && !t.TerminatedAt.HasValue
                                && (!t.EndDate.HasValue
                                    || t.EndDate.Value >= nowUtc
                                    || t.EndBehavior == TenancyEndBehaviorEnum.ContinueMonthToMonth))
                            .ToList();
                        foreach (var tenancy in tenancies)
                        {
                            var apartment = tenancy.Apartment!;
                            var property = apartment.Property!;
                            var primaryTenant = tenancy.Members
                                .Where(member => !member.IsDeleted && member.Role == TenancyMemberRoleEnum.MainTenant)
                                .Select(member => member.Member)
                                .FirstOrDefault(member => member != null)
                                ?? tenancy.Members
                                    .Where(member => !member.IsDeleted)
                                    .Select(member => member.Member)
                                    .FirstOrDefault(member => member != null);

                            // Get reminder settings for the property or landlord
                            ReminderSettings? settings = null;
                            settings = db.ReminderSettings
                                .FirstOrDefault(rs => rs.PropertyId == apartment.PropertyId);
                            if (settings == null)
                            {
                                settings = db.ReminderSettings
                                    .FirstOrDefault(rs => rs.PropertyId == null && rs.LandlordId == property.LandlordId);
                            }

                            int dueDays = apartment.RentReminderDaysBeforeDue > 0
                                ? apartment.RentReminderDaysBeforeDue
                                : settings?.RentDueReminderDays ?? 10;
                            int unpaidDays = settings?.RentUnpaidReminderDays ?? 5;

                            var leaseTerminationReminderDays = apartment.LeaseTerminationReminderDaysBeforeEnd > 0
                                ? apartment.LeaseTerminationReminderDaysBeforeEnd
                                : 30;

                            var nextRentPeriod = ResolveNextActionableRentPeriod(tenancy, nowUtc);
                            if (nextRentPeriod == null || primaryTenant == null)
                            {
                                continue;
                            }

                            var nextDue = nextRentPeriod.DueDate;
                            var rentReminderContext = new RentReminderContext(
                                primaryTenant,
                                tenancy,
                                apartment,
                                property,
                                nextRentPeriod,
                                ResolveRentPeriodStatus(nextRentPeriod, nowUtc),
                                CalculateOutstandingBalance(tenancy, nowUtc),
                                BuildTenantDashboardUrl());

                            // Send upcoming due reminder
                            var daysUntilDue = (nextDue.Date - nowUtc.Date).TotalDays;
                            if (daysUntilDue > 0 && daysUntilDue <= dueDays)
                            {
                                await SendReminderAsync(rentReminderContext, false, emailService, smsService);
                            }

                            // Send unpaid reminder if payment has not been made X days after due
                            var daysSinceDue = (nowUtc.Date - nextDue.Date).TotalDays;
                            if (daysSinceDue > 0 && daysSinceDue >= unpaidDays)
                            {
                                if (nextDue <= nowUtc)
                                {
                                    await SendReminderAsync(rentReminderContext, true, emailService, smsService);
                                }
                            }

                            if (tenancy.EndDate.HasValue)
                            {
                                var endDate = tenancy.EndDate.Value;
                                var daysUntilEnd = (endDate - nowUtc).TotalDays;
                                if (daysUntilEnd > 0 && daysUntilEnd <= leaseTerminationReminderDays)
                                {
                                    await SendLeaseTerminationReminderAsync(primaryTenant, endDate, emailService, smsService);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred while sending rent reminders.");
                }
                // Wait until the next run
                await Task.Delay(_interval, stoppingToken);
            }
        }

        private async Task SendReminderAsync(RentReminderContext context, bool isUnpaid, IEmailService emailService, ISmsService smsService)
        {
            try
            {
                var tenant = context.Tenant;
                var subject = isUnpaid
                    ? $"Rent Payment Overdue - {context.Apartment.Name}"
                    : $"Rent Payment Reminder - {context.Apartment.Name}";
                var message = BuildRentReminderMessage(context, isUnpaid);
                var smsMessage = BuildRentReminderSms(context, isUnpaid);

                // Send email
                if (!string.IsNullOrEmpty(tenant.Email))
                {
                    await emailService.SendEmailAsync(tenant.Email, subject, message);
                }
                // Send SMS if tenant has a phone number on record
                if (!string.IsNullOrEmpty(tenant.PhoneNumber))
                {
                    await smsService.SendSmsAsync(tenant.PhoneNumber, smsMessage);
                }
            }
            catch
            {
                // Ignore failures; errors will be logged by the caller.
            }
        }

        private async Task SendLeaseTerminationReminderAsync(ApplicationUser tenant, DateTimeOffset endDate, IEmailService emailService, ISmsService smsService)
        {
            try
            {
                const string subject = "Lease Termination Reminder";
                var message = $"Your lease is scheduled to end on {endDate:yyyy-MM-dd}. Please review renewal or move-out arrangements before that date.";

                if (!string.IsNullOrEmpty(tenant.Email))
                {
                    await emailService.SendEmailAsync(tenant.Email, subject, message);
                }

                if (!string.IsNullOrEmpty(tenant.PhoneNumber))
                {
                    await smsService.SendSmsAsync(tenant.PhoneNumber, message);
                }
            }
            catch
            {
                // Ignore failures; errors will be logged by the caller.
            }
        }

        private static RentPeriod? ResolveNextActionableRentPeriod(Tenancy tenancy, DateTimeOffset nowUtc)
        {
            return tenancy.RentPeriods
                .Where(period => !period.IsDeleted)
                .OrderBy(period => period.PeriodStart)
                .FirstOrDefault(period =>
                {
                    var status = ResolveRentPeriodStatus(period, nowUtc);
                    return !RentPeriodScheduleHelper.IsPaidStatus(status)
                        && status != RentPeriodStatusEnum.PendingPayment;
                });
        }

        private static RentPeriodStatusEnum ResolveRentPeriodStatus(RentPeriod period, DateTimeOffset nowUtc)
        {
            if (RentPeriodScheduleHelper.IsPaidStatus(period.Status) ||
                period.Status == RentPeriodStatusEnum.PendingPayment)
            {
                return period.Status;
            }

            return RentPeriodScheduleHelper.ResolveUnpaidStatus(period.DueDate, nowUtc);
        }

        private static decimal CalculateOutstandingBalance(Tenancy tenancy, DateTimeOffset nowUtc)
        {
            return tenancy.RentPeriods
                .Where(period => !period.IsDeleted)
                .Where(period =>
                {
                    var status = ResolveRentPeriodStatus(period, nowUtc);
                    return !RentPeriodScheduleHelper.IsPaidStatus(status)
                        && status != RentPeriodStatusEnum.PendingPayment;
                })
                .Sum(period => period.Amount > period.PaidAmount ? period.Amount - period.PaidAmount : 0);
        }

        private string BuildTenantDashboardUrl()
        {
            var portalBaseUrl = (_configuration["Portal:BaseUrl"] ?? "https://localhost:7059").Trim().TrimEnd('/');
            return $"{portalBaseUrl}/Tenant";
        }

        private static string BuildRentReminderMessage(RentReminderContext context, bool isUnpaid)
        {
            var greetingName = string.IsNullOrWhiteSpace(context.Tenant.FullName)
                ? "there"
                : context.Tenant.FullName.Trim();
            var statusLine = isUnpaid
                ? $"Your rent period {FormatPeriod(context.RentPeriod)} is overdue."
                : $"Your rent period {FormatPeriod(context.RentPeriod)} is due soon.";

            return string.Join(Environment.NewLine, new[]
            {
                $"Hello {greetingName},",
                string.Empty,
                statusLine,
                $"Due date: {FormatDate(context.RentPeriod.DueDate)}",
                $"Property: {context.Property.Name}",
                $"Apartment: {context.Apartment.Name}",
                $"Country: {FormatCountry(context.Property)}",
                $"Amount for this period: {FormatMoney(context.RentPeriod.Amount)}",
                $"Outstanding balance: {FormatMoney(context.OutstandingBalance)}",
                string.Empty,
                "Rent payments must be completed in order. RentHub will start with the oldest unpaid period.",
                $"Pay here: {context.DashboardUrl}",
                string.Empty,
                "Thank you,"
            });
        }

        private static string BuildRentReminderSms(RentReminderContext context, bool isUnpaid)
        {
            var state = isUnpaid ? "overdue" : "due soon";
            return $"Lontsi Homes: Rent for {context.Property.Name} - {context.Apartment.Name}, period {FormatPeriod(context.RentPeriod)}, is {state}. Pay: {context.DashboardUrl}";
        }

        private static string FormatCountry(Property property)
        {
            var isoCode = property.CountryIsoCode?.Trim().ToUpperInvariant();
            var phoneCode = property.CountryCode?.Trim();

            if (!string.IsNullOrWhiteSpace(isoCode) && !string.IsNullOrWhiteSpace(phoneCode))
            {
                return $"{isoCode} ({phoneCode})";
            }

            if (!string.IsNullOrWhiteSpace(isoCode))
            {
                return isoCode;
            }

            return string.IsNullOrWhiteSpace(phoneCode) ? "Not provided" : phoneCode;
        }

        private static string FormatPeriod(RentPeriod period)
        {
            return $"{FormatDate(period.PeriodStart)} - {FormatDate(period.PeriodEnd)}";
        }

        private static string FormatDate(DateTimeOffset value)
        {
            return value.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        }

        private static string FormatMoney(decimal value)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        private sealed class RentReminderContext
        {
            public RentReminderContext(
                ApplicationUser tenant,
                Tenancy tenancy,
                Apartment apartment,
                Property property,
                RentPeriod rentPeriod,
                RentPeriodStatusEnum rentPeriodStatus,
                decimal outstandingBalance,
                string dashboardUrl)
            {
                Tenant = tenant;
                Tenancy = tenancy;
                Apartment = apartment;
                Property = property;
                RentPeriod = rentPeriod;
                RentPeriodStatus = rentPeriodStatus;
                OutstandingBalance = outstandingBalance;
                DashboardUrl = dashboardUrl;
            }

            public ApplicationUser Tenant { get; }
            public Tenancy Tenancy { get; }
            public Apartment Apartment { get; }
            public Property Property { get; }
            public RentPeriod RentPeriod { get; }
            public RentPeriodStatusEnum RentPeriodStatus { get; }
            public decimal OutstandingBalance { get; }
            public string DashboardUrl { get; }
        }
    }
}

