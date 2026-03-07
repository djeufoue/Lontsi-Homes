using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentHub.API.Data;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Email;
using RentHub.API.Services.Sms;
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
                        // Find active tenancies (not ended and not deleted)
                        var tenancies = db.Tenancies
                            .Where(t => !t.IsDeleted && (!t.EndDate.HasValue || t.EndDate.Value >= DateTimeOffset.UtcNow))
                            .ToList();
                        foreach (var tenancy in tenancies)
                        {
                            // Determine next due date.  For demonstration we assume rent is due
                            // monthly on the anniversary of the start date.  This calculation
                            // approximates the next due date by adding months until after today.
                            var start = tenancy.StartDate.Date;
                            var nextDue = start;
                            while (nextDue <= DateTimeOffset.UtcNow) { nextDue = nextDue.AddMonths(1); }
                            // Get reminder settings for the property or landlord
                            ReminderSettings? settings = null;
                            if (tenancy.Apartment != null)
                            {
                                settings = db.ReminderSettings
                                    .FirstOrDefault(rs => rs.PropertyId == tenancy.Apartment.PropertyId);
                                if (settings == null)
                                {
                                    settings = db.ReminderSettings
                                        .FirstOrDefault(rs => rs.PropertyId == null && rs.LandlordId == tenancy.Apartment.Property!.LandlordId);
                                }
                            }
                            int dueDays = settings?.RentDueReminderDays ?? 10;
                            int unpaidDays = settings?.RentUnpaidReminderDays ?? 5;
                            // Send upcoming due reminder
                            var daysUntilDue = (nextDue - DateTimeOffset.UtcNow).TotalDays;
                            if (daysUntilDue > 0 && daysUntilDue <= dueDays)
                            {
                                await SendReminderAsync(tenancy, nextDue, false, emailService, smsService);
                            }
                            // Send unpaid reminder if payment has not been made X days after due
                            var daysSinceDue = (DateTimeOffset.UtcNow - nextDue).TotalDays;
                            if (daysSinceDue > 0 && daysSinceDue >= unpaidDays)
                            {
                                // Check if payment exists for this tenancy after due date
                                var hasPayment = db.Payments.Any(p => p.TenancyId == tenancy.Id && !p.IsDeleted && p.PaymentDate >= nextDue);
                                if (!hasPayment)
                                {
                                    await SendReminderAsync(tenancy, nextDue, true, emailService, smsService);
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

        private async Task SendReminderAsync(Tenancy tenancy, DateTimeOffset dueDate, bool isUnpaid, IEmailService emailService, ISmsService smsService)
        {
            try
            {
                // Compose message
                string subject = isUnpaid ? "Rent Payment Overdue" : "Rent Payment Reminder";
                string message = isUnpaid
                    ? $"Your rent payment was due on {dueDate:yyyy-MM-dd} and is now overdue. Please settle your rent as soon as possible."
                    : $"Your rent is due on {dueDate:yyyy-MM-dd}. Please ensure payment is made before the due date.";
                // Send email
                if (!string.IsNullOrEmpty(tenancy.Tenant?.Email))
                {
                    await emailService.SendEmailAsync(tenancy.Tenant.Email, subject, message);
                }
                // Send SMS if tenant has a phone number on record
                if (!string.IsNullOrEmpty(tenancy.Tenant?.PhoneNumber))
                {
                    await smsService.SendSmsAsync(tenancy.Tenant.PhoneNumber, message);
                }
            }
            catch
            {
                // Ignore failures; errors will be logged by the caller.
            }
        }
    }
}