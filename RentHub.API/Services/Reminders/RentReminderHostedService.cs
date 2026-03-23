using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentHub.API.Data;
using RentHub.API.Helpers;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Email;
using RentHub.API.Services.Sms;
using Common.Enums;
using System;
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
        private readonly TimeSpan _interval;

        public RentReminderHostedService(IServiceProvider serviceProvider, ILogger<RentReminderHostedService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
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
                            .Where(t => !t.IsDeleted && (!t.EndDate.HasValue || t.EndDate.Value >= nowUtc))
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

                            var payments = db.Payments
                                .Where(payment => payment.TenancyId == tenancy.Id && payment.Status == PaymentStatusEnum.Success)
                                .ToList();

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

                            var snapshot = TenancyReminderHelpers.BuildSnapshot(
                                tenancy,
                                apartment,
                                payments,
                                dueDays,
                                leaseTerminationReminderDays,
                                nowUtc);

                            if (!snapshot.NextRentDueDate.HasValue || primaryTenant == null)
                            {
                                continue;
                            }

                            var nextDue = snapshot.NextRentDueDate.Value;
                            // Send upcoming due reminder
                            var daysUntilDue = (nextDue - nowUtc).TotalDays;
                            if (daysUntilDue > 0 && daysUntilDue <= dueDays)
                            {
                                await SendReminderAsync(primaryTenant, nextDue, false, emailService, smsService);
                            }

                            // Send unpaid reminder if payment has not been made X days after due
                            var daysSinceDue = (nowUtc - nextDue).TotalDays;
                            if (daysSinceDue > 0 && daysSinceDue >= unpaidDays)
                            {
                                if (snapshot.NextRentDueDate.Value <= nowUtc)
                                {
                                    await SendReminderAsync(primaryTenant, nextDue, true, emailService, smsService);
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

        private async Task SendReminderAsync(ApplicationUser tenant, DateTimeOffset dueDate, bool isUnpaid, IEmailService emailService, ISmsService smsService)
        {
            try
            {
                // Compose message
                string subject = isUnpaid ? "Rent Payment Overdue" : "Rent Payment Reminder";
                string message = isUnpaid
                    ? $"Your rent payment was due on {dueDate:yyyy-MM-dd} and is now overdue. Please settle your rent as soon as possible."
                    : $"Your rent is due on {dueDate:yyyy-MM-dd}. Please ensure payment is made before the due date.";
                // Send email
                if (!string.IsNullOrEmpty(tenant.Email))
                {
                    await emailService.SendEmailAsync(tenant.Email, subject, message);
                }
                // Send SMS if tenant has a phone number on record
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
    }
}

