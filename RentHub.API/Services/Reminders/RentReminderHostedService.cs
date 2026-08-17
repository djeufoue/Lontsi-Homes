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
using RentHub.API.Services.Tenancies;
using RentHub.API.Helpers;
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
                        var renewalEmailService = scope.ServiceProvider.GetRequiredService<ITenancyRenewalEmailService>();
                        var nowUtc = DateTimeOffset.UtcNow;
                        var platformAutomaticPaymentsEnabled = await PaymentAvailabilityHelper.IsPlatformAutomaticPaymentEnabledAsync(db);
                        // Find active tenancies (not ended and not deleted)
                        var tenancies = await db.Tenancies
                            .Include(t => t.Members)
                            .ThenInclude(m => m.Member)
                            .Include(t => t.Apartment)
                            .ThenInclude(a => a!.Property)
                            .Include(t => t.RentPeriods)
                            .Include(t => t.ExtensionRequests)
                            .Where(t => !t.IsDeleted
                                && !t.TerminatedAt.HasValue
                                && (!t.EndDate.HasValue
                                    || t.EndDate.Value >= nowUtc
                                    || t.EndBehavior == TenancyEndBehaviorEnum.ContinueMonthToMonth))
                            .ToListAsync(stoppingToken);
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

                            if (primaryTenant == null)
                            {
                                continue;
                            }

                            if (tenancy.EndDate.HasValue)
                            {
                                var endDate = tenancy.EndDate.Value;
                                var daysUntilEnd = (endDate.Date - nowUtc.Date).TotalDays;
                                var alreadySentForCurrentEndDate =
                                    tenancy.RenewalReminderSentForEndDate.HasValue &&
                                    tenancy.RenewalReminderSentForEndDate.Value.Date == endDate.Date;
                                var hasPendingRenewal = tenancy.ExtensionRequests.Any(request =>
                                    !request.IsDeleted && request.Status == TenancyExtensionStatusEnum.Pending);

                                if (daysUntilEnd > 0 &&
                                    daysUntilEnd <= leaseTerminationReminderDays &&
                                    !alreadySentForCurrentEndDate &&
                                    !hasPendingRenewal &&
                                    !string.IsNullOrWhiteSpace(primaryTenant.Email))
                                {
                                    await renewalEmailService.SendExpiryReminderAsync(primaryTenant, tenancy, stoppingToken);
                                    if (!string.IsNullOrWhiteSpace(primaryTenant.PhoneNumber))
                                    {
                                        var renewalUrl = BuildTenancyRenewalUrl(tenancy.Id);
                                        var formattedEndDate = endDate.ToString(
                                            "d",
                                            CultureInfo.GetCultureInfo(primaryTenant.Language.ToCultureName()));
                                        var smsMessage = primaryTenant.Language == PlatformLanguage.French
                                            ? $"Lontsi Homes : votre bail prend fin le {formattedEndDate}. Demandez un renouvellement : {renewalUrl}"
                                            : $"Lontsi Homes: your tenancy ends on {formattedEndDate}. Request a renewal: {renewalUrl}";
                                        await smsService.SendSmsAsync(primaryTenant.PhoneNumber, smsMessage);
                                    }

                                    tenancy.RenewalReminderSentAt = nowUtc;
                                    tenancy.RenewalReminderSentForEndDate = endDate;
                                    tenancy.UpdatedBy = "system-renewal-reminder";
                                    tenancy.UpdatedAt = nowUtc;
                                    await db.SaveChangesAsync(stoppingToken);
                                }
                            }

                            var nextRentPeriod = ResolveNextActionableRentPeriod(tenancy, nowUtc);
                            if (nextRentPeriod == null)
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
                                BuildTenantDashboardUrl(),
                                BuildTenantPaymentDetailsUrl(tenancy.Id),
                                platformAutomaticPaymentsEnabled && property.AutomaticPaymentsEnabled);

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
                var isFrench = tenant.EmailLanguage == PlatformLanguage.French;
                var subject = isFrench
                    ? (isUnpaid ? $"Loyer en retard – {context.Apartment.Name}" : $"Rappel de paiement du loyer – {context.Apartment.Name}")
                    : (isUnpaid ? $"Rent Payment Overdue - {context.Apartment.Name}" : $"Rent Payment Reminder - {context.Apartment.Name}");
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

        private string BuildTenantPaymentDetailsUrl(int tenancyId)
        {
            var portalBaseUrl = (_configuration["Portal:BaseUrl"] ?? "https://localhost:7059").Trim().TrimEnd('/');
            return $"{portalBaseUrl}/Tenant/PaymentDetails?tenancyId={tenancyId}";
        }

        private string BuildTenancyRenewalUrl(int tenancyId)
        {
            var portalBaseUrl = (_configuration["Portal:BaseUrl"] ?? "https://localhost:7059").Trim().TrimEnd('/');
            return $"{portalBaseUrl}/Tenancies/Renewal?tenancyId={tenancyId}";
        }

        private static string BuildRentReminderMessage(RentReminderContext context, bool isUnpaid)
        {
            var isFrench = context.Tenant.EmailLanguage == PlatformLanguage.French;
            var greetingName = string.IsNullOrWhiteSpace(context.Tenant.FullName)
                ? (isFrench ? string.Empty : "there")
                : context.Tenant.FullName.Trim();
            var statusLine = isFrench
                ? (isUnpaid
                    ? $"Votre période de loyer du {FormatPeriod(context.RentPeriod, context.Tenant.EmailLanguage)} est en retard."
                    : $"Votre période de loyer du {FormatPeriod(context.RentPeriod, context.Tenant.EmailLanguage)} arrive bientôt à échéance.")
                : (isUnpaid
                    ? $"Your rent period {FormatPeriod(context.RentPeriod, context.Tenant.EmailLanguage)} is overdue."
                    : $"Your rent period {FormatPeriod(context.RentPeriod, context.Tenant.EmailLanguage)} is due soon.");

            var paymentAction = isFrench
                ? (context.AutomaticPaymentsEnabled
                    ? $"Payer ici : {context.DashboardUrl}"
                    : $"Le paiement automatique est temporairement indisponible. Consultez les détails ici : {context.PaymentDetailsUrl}")
                : (context.AutomaticPaymentsEnabled
                    ? $"Pay here: {context.DashboardUrl}"
                    : $"Automatic payment is temporarily unavailable. Review payment details here: {context.PaymentDetailsUrl}");

            if (isFrench)
            {
                return string.Join(Environment.NewLine, new[]
                {
                    $"Bonjour {greetingName},",
                    string.Empty,
                    statusLine,
                    $"Date d’échéance : {FormatDate(context.RentPeriod.DueDate, context.Tenant.EmailLanguage)}",
                    $"Propriété : {context.Property.Name}",
                    $"Appartement : {context.Apartment.Name}",
                    $"Pays : {FormatCountry(context.Property, true)}",
                    $"Montant de cette période : {FormatMoney(context.RentPeriod.Amount, context.Tenant.EmailLanguage)}",
                    $"Solde impayé : {FormatMoney(context.OutstandingBalance, context.Tenant.EmailLanguage)}",
                    string.Empty,
                    "Les loyers doivent être payés dans l’ordre. Lontsi Homes commencera par la plus ancienne période impayée.",
                    paymentAction,
                    string.Empty,
                    "Merci,"
                });
            }

            return string.Join(Environment.NewLine, new[]
            {
                $"Hello {greetingName},",
                string.Empty,
                statusLine,
                $"Due date: {FormatDate(context.RentPeriod.DueDate, context.Tenant.EmailLanguage)}",
                $"Property: {context.Property.Name}",
                $"Apartment: {context.Apartment.Name}",
                $"Country: {FormatCountry(context.Property, false)}",
                $"Amount for this period: {FormatMoney(context.RentPeriod.Amount, context.Tenant.EmailLanguage)}",
                $"Outstanding balance: {FormatMoney(context.OutstandingBalance, context.Tenant.EmailLanguage)}",
                string.Empty,
                "Rent payments must be completed in order. RentHub will start with the oldest unpaid period.",
                paymentAction,
                string.Empty,
                "Thank you,"
            });
        }

        private static string BuildRentReminderSms(RentReminderContext context, bool isUnpaid)
        {
            var state = isUnpaid ? "overdue" : "due soon";
            var action = context.AutomaticPaymentsEnabled
                ? $"Pay: {context.DashboardUrl}"
                : $"Details: {context.PaymentDetailsUrl}";
            return $"Lontsi Homes: Rent for {context.Property.Name} - {context.Apartment.Name}, period {FormatPeriod(context.RentPeriod)}, is {state}. {action}";
        }

        private static string FormatCountry(Property property, bool isFrench = false)
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

            return string.IsNullOrWhiteSpace(phoneCode) ? (isFrench ? "Non fourni" : "Not provided") : phoneCode;
        }

        private static string FormatPeriod(RentPeriod period, PlatformLanguage language = PlatformLanguage.English)
        {
            return $"{FormatDate(period.PeriodStart, language)} - {FormatDate(period.PeriodEnd, language)}";
        }

        private static string FormatDate(DateTimeOffset value, PlatformLanguage language = PlatformLanguage.English)
        {
            return value.ToString("dd MMM yyyy", CultureInfo.GetCultureInfo(language.ToCultureName()));
        }

        private static string FormatMoney(decimal value, PlatformLanguage language = PlatformLanguage.English)
        {
            return value.ToString("N0", CultureInfo.GetCultureInfo(language.ToCultureName()));
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
                string dashboardUrl,
                string paymentDetailsUrl,
                bool automaticPaymentsEnabled)
            {
                Tenant = tenant;
                Tenancy = tenancy;
                Apartment = apartment;
                Property = property;
                RentPeriod = rentPeriod;
                RentPeriodStatus = rentPeriodStatus;
                OutstandingBalance = outstandingBalance;
                DashboardUrl = dashboardUrl;
                PaymentDetailsUrl = paymentDetailsUrl;
                AutomaticPaymentsEnabled = automaticPaymentsEnabled;
            }

            public ApplicationUser Tenant { get; }
            public Tenancy Tenancy { get; }
            public Apartment Apartment { get; }
            public Property Property { get; }
            public RentPeriod RentPeriod { get; }
            public RentPeriodStatusEnum RentPeriodStatus { get; }
            public decimal OutstandingBalance { get; }
            public string DashboardUrl { get; }
            public string PaymentDetailsUrl { get; }
            public bool AutomaticPaymentsEnabled { get; }
        }
    }
}

