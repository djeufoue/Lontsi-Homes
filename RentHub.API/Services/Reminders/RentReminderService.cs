using Common.CommunicationModels;
using Common.Enums;
using Common.Helpers;
using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Helpers;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Email;
using RentHub.API.Services.Sms;
using RentHub.API.Services.Tenancies;
using System.Data;
using System.Globalization;
using System.Net;
using System.Text;

namespace RentHub.API.Services.Reminders
{
    public interface IRentReminderService
    {
        Task ProcessDailyRemindersAsync();
        Task SendClaimedReminderAsync(int reminderId);
        Task<SendManualRentReminderResultDto> SendManualReminderAsync(
            int tenancyId,
            string requestedByUserId,
            CancellationToken cancellationToken = default);
    }

    public sealed class RentReminderService : IRentReminderService
    {
        private const string Currency = "XAF";
        private const string SystemUser = "system:rent-reminder";
        private const int TriggerKeyQueryBatchSize = 1000;

        private readonly ApplicationDbContext _context;
        private readonly IEmailService _emailService;
        private readonly ISmsService _smsService;
        private readonly ITenancyRenewalEmailService _renewalEmailService;
        private readonly IBackgroundJobClient _backgroundJobs;
        private readonly IConfiguration _configuration;
        private readonly ILogger<RentReminderService> _logger;

        public RentReminderService(
            ApplicationDbContext context,
            IEmailService emailService,
            ISmsService smsService,
            ITenancyRenewalEmailService renewalEmailService,
            IBackgroundJobClient backgroundJobs,
            IConfiguration configuration,
            ILogger<RentReminderService> logger)
        {
            _context = context;
            _emailService = emailService;
            _smsService = smsService;
            _renewalEmailService = renewalEmailService;
            _backgroundJobs = backgroundJobs;
            _configuration = configuration;
            _logger = logger;
        }

        [AutomaticRetry(Attempts = 1)]
        public async Task ProcessDailyRemindersAsync()
        {
            await ClaimAutomaticRentRemindersAsync();
            await ProcessLeaseExpiryRemindersAsync();
        }

        private async Task ClaimAutomaticRentRemindersAsync()
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var today = nowUtc.Date;

            var baseQuery =
                from rule in _context.ApartmentRentReminderRules.AsNoTracking()
                join tenancy in _context.Tenancies.AsNoTracking()
                    on rule.ApartmentId equals tenancy.ApartmentId
                join period in _context.RentPeriods.AsNoTracking()
                    on tenancy.Id equals period.TenancyId
                where rule.IsEnabled
                      && (rule.EmailEnabled || rule.SmsEnabled)
                      && !tenancy.RentScheduleNeedsReview
                      && (!tenancy.TerminatedAt.HasValue || tenancy.TerminatedAt.Value.Date > today)
                      && (!tenancy.EndDate.HasValue
                          || tenancy.EndDate.Value >= nowUtc
                          || tenancy.EndBehavior == TenancyEndBehaviorEnum.ContinueMonthToMonth)
                      && period.Amount > period.PaidAmount
                      && (period.Status == RentPeriodStatusEnum.NotDueYet
                          || period.Status == RentPeriodStatusEnum.Due
                          || period.Status == RentPeriodStatusEnum.Overdue)
                select new { rule, tenancy, period };

            var candidates = await baseQuery
                .Where(item =>
                    (item.rule.Timing == RentReminderTimingEnum.BeforeDue
                     && item.period.DueDate.Date > today
                     && item.period.DueDate.AddDays(-item.rule.Days).Date <= today)
                    || (item.rule.Timing == RentReminderTimingEnum.OnDueDate
                        && item.period.DueDate.Date == today)
                    || (item.rule.Timing == RentReminderTimingEnum.AfterDue
                        && item.period.DueDate.AddDays(item.rule.Days).Date <= today))
                .Select(item => new AutomaticReminderCandidate(
                    item.tenancy.Id,
                    item.rule.Id,
                    item.period.Id,
                    item.rule.Timing,
                    item.rule.Days,
                    item.rule.EmailEnabled,
                    item.rule.SmsEnabled,
                    item.rule.Timing == RentReminderTimingEnum.BeforeDue
                        ? item.period.DueDate.AddDays(-item.rule.Days)
                        : item.rule.Timing == RentReminderTimingEnum.AfterDue
                            ? item.period.DueDate.AddDays(item.rule.Days)
                            : item.period.DueDate,
                    item.period.DueDate,
                    item.tenancy.UpdatedAt ?? item.tenancy.CreatedAt,
                    string.Empty))
                .ToListAsync();

            candidates = candidates
                .Select(candidate => candidate with
                {
                    TriggerKey = $"auto:{candidate.RuleId}:{candidate.RentPeriodId}:{candidate.DueDate.UtcTicks}:{candidate.ScheduleVersion.UtcTicks}"
                })
                .ToList();

            if (candidates.Count == 0)
            {
                return;
            }

            var existingTriggerKeys = await LoadExistingTriggerKeysAsync(
                candidates.Select(candidate => candidate.TriggerKey).ToList());
            candidates = candidates
                .Where(candidate => !existingTriggerKeys.Contains(candidate.TriggerKey))
                .ToList();

            if (candidates.Count == 0)
            {
                return;
            }

            var tenancyIds = candidates.Select(candidate => candidate.TenancyId).Distinct().ToList();
            var tenancies = await _context.Tenancies
                .AsNoTracking()
                .Include(tenancy => tenancy.Apartment)
                .ThenInclude(apartment => apartment!.Property)
                .Include(tenancy => tenancy.Members)
                .ThenInclude(member => member.Member)
                .Where(tenancy => tenancyIds.Contains(tenancy.Id))
                .ToListAsync();
            var tenancyById = tenancies.ToDictionary(tenancy => tenancy.Id);

            var openPeriods = await _context.RentPeriods
                .AsNoTracking()
                .Where(period => tenancyIds.Contains(period.TenancyId)
                                 && period.Amount > period.PaidAmount
                                 && (period.Status == RentPeriodStatusEnum.NotDueYet
                                     || period.Status == RentPeriodStatusEnum.Due
                                     || period.Status == RentPeriodStatusEnum.Overdue))
                .OrderBy(period => period.PeriodStart)
                .ToListAsync();
            var periodsByTenancy = openPeriods
                .GroupBy(period => period.TenancyId)
                .ToDictionary(group => group.Key, group => group.ToList());

            var reminders = new List<RentReminder>();

            foreach (var group in candidates.GroupBy(candidate => candidate.TenancyId))
            {
                if (!tenancyById.TryGetValue(group.Key, out var tenancy)
                    || tenancy.Apartment?.Property == null
                    || !periodsByTenancy.TryGetValue(group.Key, out var periods))
                {
                    _logger.LogWarning(
                        "Skipping rent reminder claim because tenancy context is incomplete for tenancy {TenancyId}.",
                        group.Key);
                    continue;
                }

                var tenant = ResolvePrimaryTenant(tenancy);
                if (tenant == null)
                {
                    _logger.LogWarning(
                        "Skipping rent reminder claim because tenancy {TenancyId} has no active member.",
                        tenancy.Id);
                    continue;
                }

                var groupCandidates = group.OrderBy(candidate => candidate.ScheduledFor).ToList();
                var triggerPeriodIds = groupCandidates.Select(candidate => candidate.RentPeriodId).ToHashSet();
                var category = ResolveBatchCategory(groupCandidates.Select(candidate => candidate.Timing));
                var requestEmail = groupCandidates.Any(candidate => candidate.EmailEnabled);
                var requestSms = groupCandidates.Any(candidate => candidate.SmsEnabled);
                var reminder = BuildReminder(
                    tenancy,
                    tenant,
                    periods,
                    triggerPeriodIds,
                    category,
                    false,
                    groupCandidates.Min(candidate => candidate.ScheduledFor),
                    requestEmail,
                    requestSms,
                    null,
                    nowUtc);

                foreach (var candidate in groupCandidates)
                {
                    reminder.Triggers.Add(new RentReminderTrigger
                    {
                        RentPeriodId = candidate.RentPeriodId,
                        ApartmentRentReminderRuleId = candidate.RuleId,
                        Category = MapCategory(candidate.Timing),
                        TriggerKey = candidate.TriggerKey,
                        CreatedAt = nowUtc
                    });
                }

                reminders.Add(reminder);
            }

            if (reminders.Count == 0)
            {
                return;
            }

            _context.RentReminders.AddRange(reminders);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // Another API instance claimed at least one identical trigger first. The unique
                // trigger key is the final concurrency guard; the next daily catch-up will claim
                // any unrelated trigger that was part of this batch.
                _logger.LogInformation(ex, "A concurrent worker already claimed one or more rent reminders.");
                _context.ChangeTracker.Clear();
                return;
            }

            foreach (var reminder in reminders)
            {
                _backgroundJobs.Enqueue<IRentReminderService>(service => service.SendClaimedReminderAsync(reminder.Id));
            }
        }

        public async Task<SendManualRentReminderResultDto> SendManualReminderAsync(
            int tenancyId,
            string requestedByUserId,
            CancellationToken cancellationToken = default)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var tenancy = await _context.Tenancies
                .AsNoTracking()
                .Include(item => item.Apartment)
                .ThenInclude(apartment => apartment!.Property)
                .Include(item => item.Members)
                .ThenInclude(member => member.Member)
                .FirstOrDefaultAsync(item => item.Id == tenancyId, cancellationToken)
                ?? throw new InvalidOperationException("Tenancy not found.");

            if (tenancy.Apartment?.Property == null)
            {
                throw new InvalidOperationException("The tenancy apartment or property is unavailable.");
            }

            if (tenancy.TerminatedAt.HasValue && tenancy.TerminatedAt.Value.Date <= nowUtc.Date)
            {
                throw new InvalidOperationException(
                    "Rent reminders are disabled on and after the tenancy termination date.");
            }

            if (tenancy.RentScheduleNeedsReview)
            {
                throw new InvalidOperationException(
                    "Rent reminders are paused until the legacy rent schedule has been reviewed.");
            }

            var tenant = ResolvePrimaryTenant(tenancy)
                ?? throw new InvalidOperationException("The tenancy has no active recipient.");
            if (string.IsNullOrWhiteSpace(tenant.Email))
            {
                throw new InvalidOperationException("The main tenant does not have an email address.");
            }

            var openPeriods = await _context.RentPeriods
                .AsNoTracking()
                .Where(period => period.TenancyId == tenancyId
                                 && period.Amount > period.PaidAmount
                                 && (period.Status == RentPeriodStatusEnum.NotDueYet
                                     || period.Status == RentPeriodStatusEnum.Due
                                     || period.Status == RentPeriodStatusEnum.Overdue))
                .OrderBy(period => period.PeriodStart)
                .ToListAsync(cancellationToken);
            var duePeriods = openPeriods.Where(period => period.DueDate.Date <= nowUtc.Date).ToList();
            var triggerPeriod = duePeriods.FirstOrDefault()
                ?? throw new InvalidOperationException("There is no unpaid rent that is currently due.");

            var apartment = tenancy.Apartment;
            if (apartment.ManualRentReminderLimit <= 0)
            {
                throw new InvalidOperationException("Manual rent reminders are disabled for this apartment.");
            }

            await using var transaction = await _context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            var manualTriggers = await _context.RentReminderTriggers
                .Where(trigger => trigger.RentPeriodId == triggerPeriod.Id
                                  && trigger.Category == RentReminderCategoryEnum.Manual)
                .Select(trigger => new
                {
                    trigger.ManualSequence,
                    trigger.RentReminder!.Status,
                    trigger.RentReminder.SentAt,
                    trigger.RentReminder.CreatedAt,
                    trigger.RentReminder.InvalidatedAt
                })
                .ToListAsync(cancellationToken);

            var countedManualReminders = manualTriggers.Count(trigger =>
                !trigger.InvalidatedAt.HasValue &&
                trigger.Status != RentReminderStatusEnum.Failed
                && trigger.Status != RentReminderStatusEnum.Cancelled);
            if (countedManualReminders >= apartment.ManualRentReminderLimit)
            {
                throw new InvalidOperationException(
                    $"Manual reminder limit reached: {countedManualReminders} of {apartment.ManualRentReminderLimit} used for this rent period.");
            }

            var lastManualSentAt = manualTriggers
                .Where(trigger => trigger.SentAt.HasValue)
                .Max(trigger => trigger.SentAt);
            if (lastManualSentAt.HasValue && apartment.ManualRentReminderCooldownHours > 0)
            {
                var nextAllowedAt = lastManualSentAt.Value.AddHours(apartment.ManualRentReminderCooldownHours);
                if (nextAllowedAt > nowUtc)
                {
                    throw new InvalidOperationException(
                        $"Another manual reminder can be sent after {nextAllowedAt:u}.");
                }
            }

            var sequence = manualTriggers.Count == 0
                ? 1
                : manualTriggers.Max(trigger => trigger.ManualSequence) + 1;
            var reminder = BuildReminder(
                tenancy,
                tenant,
                openPeriods,
                new HashSet<int> { triggerPeriod.Id },
                RentReminderCategoryEnum.Manual,
                true,
                nowUtc,
                true,
                false,
                requestedByUserId,
                nowUtc);
            reminder.Triggers.Add(new RentReminderTrigger
            {
                RentPeriodId = triggerPeriod.Id,
                Category = RentReminderCategoryEnum.Manual,
                ManualSequence = sequence,
                TriggerKey = $"manual:{triggerPeriod.Id}:{sequence}",
                CreatedAt = nowUtc
            });

            _context.RentReminders.Add(reminder);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _context.ChangeTracker.Clear();
            try
            {
                await SendClaimedReminderAsync(reminder.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Immediate manual rent reminder {ReminderId} failed and was queued for retry.", reminder.Id);
                _backgroundJobs.Enqueue<IRentReminderService>(service => service.SendClaimedReminderAsync(reminder.Id));
            }

            var savedStatus = await _context.RentReminders
                .AsNoTracking()
                .Where(item => item.Id == reminder.Id)
                .Select(item => item.Status)
                .SingleAsync(cancellationToken);

            return new SendManualRentReminderResultDto
            {
                ReminderId = reminder.Id,
                Status = savedStatus,
                ManualReminderCount = countedManualReminders + 1,
                ManualReminderLimit = apartment.ManualRentReminderLimit,
                Message = savedStatus == RentReminderStatusEnum.Sent
                    ? "Manual rent reminder sent."
                    : "The reminder was recorded and queued for another delivery attempt."
            };
        }

        [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 300, 1800, 7200 })]
        public async Task SendClaimedReminderAsync(int reminderId)
        {
            var reminder = await _context.RentReminders
                .Include(item => item.Triggers)
                .ThenInclude(trigger => trigger.RentPeriod)
                .Include(item => item.Triggers)
                .ThenInclude(trigger => trigger.ApartmentRentReminderRule)
                .Include(item => item.Periods)
                .Include(item => item.Tenancy)
                .ThenInclude(tenancy => tenancy!.Apartment)
                .ThenInclude(apartment => apartment!.Property)
                .Include(item => item.Tenancy)
                .ThenInclude(tenancy => tenancy!.Members)
                .ThenInclude(member => member.Member)
                .AsSplitQuery()
                .FirstOrDefaultAsync(item => item.Id == reminderId);
            if (reminder == null || reminder.Status == RentReminderStatusEnum.Sent)
            {
                return;
            }

            var nowUtc = DateTimeOffset.UtcNow;
            var invalidReason = ValidateReminderForDelivery(reminder, nowUtc);
            if (!string.IsNullOrWhiteSpace(invalidReason))
            {
                reminder.Status = RentReminderStatusEnum.Cancelled;
                reminder.EmailStatus = reminder.EmailStatus == ReminderDeliveryStatusEnum.Pending
                    ? ReminderDeliveryStatusEnum.Skipped
                    : reminder.EmailStatus;
                reminder.SmsStatus = reminder.SmsStatus == ReminderDeliveryStatusEnum.Pending
                    ? ReminderDeliveryStatusEnum.Skipped
                    : reminder.SmsStatus;
                reminder.FailureReason = invalidReason;
                reminder.InvalidatedAt ??= nowUtc;
                reminder.InvalidationReason = invalidReason.Truncate(512);
                reminder.UpdatedAt = nowUtc;
                await _context.SaveChangesAsync();
                return;
            }

            if (reminder.EmailStatus != ReminderDeliveryStatusEnum.Sent &&
                reminder.SmsStatus != ReminderDeliveryStatusEnum.Sent)
            {
                await RefreshReminderContentAsync(reminder, nowUtc);
            }

            var failures = new List<string>();
            if (reminder.EmailStatus is ReminderDeliveryStatusEnum.Pending or ReminderDeliveryStatusEnum.Failed)
            {
                reminder.EmailAttemptCount++;
                var result = await _emailService.TrySendEmailAsync(new EmailMessage
                {
                    To = reminder.RecipientEmail,
                    Subject = reminder.Subject,
                    PlainTextBody = reminder.PlainTextBody,
                    HtmlBody = reminder.HtmlBody
                });
                reminder.EmailStatus = result.Succeeded
                    ? ReminderDeliveryStatusEnum.Sent
                    : ReminderDeliveryStatusEnum.Failed;
                if (!result.Succeeded)
                {
                    failures.Add($"Email: {result.Error}");
                }
            }

            if (reminder.SmsStatus is ReminderDeliveryStatusEnum.Pending or ReminderDeliveryStatusEnum.Failed)
            {
                reminder.SmsAttemptCount++;
                var result = await _smsService.TrySendSmsAsync(reminder.RecipientPhone, BuildSms(reminder));
                reminder.SmsStatus = result.Succeeded
                    ? ReminderDeliveryStatusEnum.Sent
                    : ReminderDeliveryStatusEnum.Failed;
                if (!result.Succeeded)
                {
                    failures.Add($"SMS: {result.Error}");
                }
            }

            var requestedStatuses = new[] { reminder.EmailStatus, reminder.SmsStatus }
                .Where(status => status != ReminderDeliveryStatusEnum.NotRequested
                                 && status != ReminderDeliveryStatusEnum.Skipped)
                .ToList();
            var sentCount = requestedStatuses.Count(status => status == ReminderDeliveryStatusEnum.Sent);
            var failedCount = requestedStatuses.Count(status => status == ReminderDeliveryStatusEnum.Failed);

            reminder.Status = requestedStatuses.Count == 0
                ? RentReminderStatusEnum.Failed
                : failedCount == 0
                    ? RentReminderStatusEnum.Sent
                    : sentCount > 0
                        ? RentReminderStatusEnum.PartiallySent
                        : RentReminderStatusEnum.Failed;
            reminder.SentAt = sentCount > 0 ? DateTimeOffset.UtcNow : reminder.SentAt;
            reminder.FailureReason = string.Join(" | ", failures).Truncate(2048);
            reminder.UpdatedAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync();

            if (failedCount > 0 || requestedStatuses.Count == 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(reminder.FailureReason)
                        ? "No reminder delivery channel was available."
                        : reminder.FailureReason);
            }
        }

        private async Task ProcessLeaseExpiryRemindersAsync()
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var latestRelevantEndDate = nowUtc.AddDays(365);
            var tenancies = await _context.Tenancies
                .Include(tenancy => tenancy.Apartment)
                .Include(tenancy => tenancy.Members)
                .ThenInclude(member => member.Member)
                .Include(tenancy => tenancy.ExtensionRequests)
                .AsSplitQuery()
                .Where(tenancy => !tenancy.TerminatedAt.HasValue
                                  && tenancy.EndDate.HasValue
                                  && tenancy.EndDate.Value.Date > nowUtc.Date
                                  && tenancy.EndDate.Value.Date <= latestRelevantEndDate.Date
                                  && (!tenancy.RenewalReminderSentForEndDate.HasValue
                                      || tenancy.RenewalReminderSentForEndDate.Value.Date != tenancy.EndDate.Value.Date)
                                  && !tenancy.ExtensionRequests.Any(request =>
                                      request.Status == TenancyExtensionStatusEnum.Pending))
                .ToListAsync();

            var changed = false;
            foreach (var tenancy in tenancies)
            {
                var days = Math.Max(0, tenancy.Apartment?.LeaseTerminationReminderDaysBeforeEnd ?? 30);
                if ((tenancy.EndDate!.Value.Date - nowUtc.Date).TotalDays > days)
                {
                    continue;
                }

                var tenant = ResolvePrimaryTenant(tenancy);
                if (tenant == null || string.IsNullOrWhiteSpace(tenant.Email))
                {
                    continue;
                }

                await _renewalEmailService.SendExpiryReminderAsync(tenant, tenancy);
                tenancy.RenewalReminderSentAt = nowUtc;
                tenancy.RenewalReminderSentForEndDate = tenancy.EndDate;
                tenancy.UpdatedBy = SystemUser;
                tenancy.UpdatedAt = nowUtc;
                changed = true;
            }

            if (changed)
            {
                await _context.SaveChangesAsync();
            }
        }

        private static string ValidateReminderForDelivery(RentReminder reminder, DateTimeOffset nowUtc)
        {
            if (reminder.InvalidatedAt.HasValue)
                return string.IsNullOrWhiteSpace(reminder.InvalidationReason)
                    ? "The reminder was invalidated before delivery."
                    : reminder.InvalidationReason;

            var tenancy = reminder.Tenancy;
            if (tenancy == null || tenancy.IsDeleted)
                return "The tenancy is no longer active.";
            if (tenancy.RentScheduleNeedsReview)
                return "The rent schedule requires review; automatic and manual reminders are paused.";
            if (tenancy.TerminatedAt.HasValue && tenancy.TerminatedAt.Value.Date <= nowUtc.Date)
                return "The tenancy ended before reminder delivery.";
            if (tenancy.EndDate.HasValue &&
                tenancy.EndBehavior != TenancyEndBehaviorEnum.ContinueMonthToMonth &&
                tenancy.EndDate.Value.Date < nowUtc.Date)
                return "The fixed-term tenancy ended before reminder delivery.";

            var hasApplicableTrigger = false;
            foreach (var trigger in reminder.Triggers)
            {
                var period = trigger.RentPeriod;
                if (period == null || period.IsDeleted) continue;

                var snapshot = reminder.Periods.FirstOrDefault(item =>
                    item.RentPeriodId == period.Id && item.IsTrigger);
                if (snapshot != null &&
                    (snapshot.PeriodStartSnapshot != period.PeriodStart ||
                     snapshot.PeriodEndSnapshot != period.PeriodEnd ||
                     snapshot.DueDateSnapshot != period.DueDate))
                {
                    return "The reminder was planned using an old rent schedule and was invalidated after correction.";
                }

                if (period.Amount <= period.PaidAmount ||
                    RentPeriodScheduleHelper.IsPaidStatus(period.Status) ||
                    period.Status == RentPeriodStatusEnum.PendingPayment)
                {
                    continue;
                }

                if (reminder.IsManual || trigger.Category == RentReminderCategoryEnum.Manual)
                {
                    hasApplicableTrigger |= period.DueDate.Date <= nowUtc.Date;
                    continue;
                }

                var rule = trigger.ApartmentRentReminderRule;
                if (rule == null || rule.IsDeleted || !rule.IsEnabled ||
                    (!rule.EmailEnabled && !rule.SmsEnabled))
                {
                    continue;
                }

                hasApplicableTrigger |= rule.Timing switch
                {
                    RentReminderTimingEnum.BeforeDue =>
                        period.DueDate.Date > nowUtc.Date &&
                        period.DueDate.AddDays(-rule.Days).Date <= nowUtc.Date,
                    RentReminderTimingEnum.OnDueDate => period.DueDate.Date == nowUtc.Date,
                    RentReminderTimingEnum.AfterDue =>
                        period.DueDate.AddDays(rule.Days).Date <= nowUtc.Date,
                    _ => false
                };
            }

            return hasApplicableTrigger
                ? string.Empty
                : "The payment, due date, or reminder rule changed before delivery; the reminder is no longer applicable.";
        }

        private async Task RefreshReminderContentAsync(RentReminder reminder, DateTimeOffset nowUtc)
        {
            var tenancy = reminder.Tenancy!;
            var tenant = ResolvePrimaryTenant(tenancy)
                ?? throw new InvalidOperationException("The tenancy has no active reminder recipient.");
            var openPeriods = await _context.RentPeriods
                .Where(period => period.TenancyId == tenancy.Id &&
                                 period.Amount > period.PaidAmount &&
                                 (period.Status == RentPeriodStatusEnum.NotDueYet ||
                                  period.Status == RentPeriodStatusEnum.Due ||
                                  period.Status == RentPeriodStatusEnum.Overdue))
                .OrderBy(period => period.PeriodStart)
                .ToListAsync();
            var duePeriods = openPeriods
                .Where(period => period.DueDate.Date <= nowUtc.Date)
                .ToList();
            var nextDueDate = openPeriods
                .Where(period => period.DueDate.Date > nowUtc.Date)
                .Select(period => (DateTimeOffset?)period.DueDate)
                .Min();
            var nextPeriods = nextDueDate.HasValue
                ? openPeriods.Where(period => period.DueDate == nextDueDate.Value).ToList()
                : new List<RentPeriod>();
            var content = BuildEmailContent(tenancy, tenant, duePeriods, nextPeriods, reminder.Category);
            reminder.Subject = content.Subject;
            reminder.PlainTextBody = content.PlainText;
            reminder.HtmlBody = content.Html;
            reminder.OutstandingAmountSnapshot = duePeriods.Sum(period =>
                Math.Max(0, period.Amount - period.PaidAmount));
            reminder.IncludedPeriodCount = duePeriods.Count;
            reminder.RecipientEmail = tenant.Email?.Trim() ?? reminder.RecipientEmail;
            reminder.RecipientPhone = tenant.PhoneNumber?.Trim() ?? reminder.RecipientPhone;

            var triggerIds = reminder.Triggers.Select(trigger => trigger.RentPeriodId).ToHashSet();
            _context.RentReminderPeriods.RemoveRange(reminder.Periods);
            reminder.Periods.Clear();
            foreach (var period in duePeriods)
            {
                reminder.Periods.Add(ToSnapshot(
                    period,
                    RentReminderPeriodRelationEnum.Outstanding,
                    triggerIds.Contains(period.Id)));
            }
            foreach (var period in nextPeriods.Where(next =>
                         reminder.Periods.All(snapshot => snapshot.RentPeriodId != next.Id)))
            {
                reminder.Periods.Add(ToSnapshot(
                    period,
                    RentReminderPeriodRelationEnum.UpcomingInformation,
                    triggerIds.Contains(period.Id)));
            }
        }

        private RentReminder BuildReminder(
            Tenancy tenancy,
            ApplicationUser tenant,
            IReadOnlyList<RentPeriod> openPeriods,
            IReadOnlySet<int> triggerPeriodIds,
            RentReminderCategoryEnum category,
            bool isManual,
            DateTimeOffset scheduledFor,
            bool requestEmail,
            bool requestSms,
            string? requestedByUserId,
            DateTimeOffset nowUtc)
        {
            var duePeriods = openPeriods
                .Where(period => period.DueDate.Date <= nowUtc.Date)
                .OrderBy(period => period.DueDate)
                .ToList();
            var nextDueDate = openPeriods
                .Where(period => period.DueDate.Date > nowUtc.Date)
                .Select(period => (DateTimeOffset?)period.DueDate)
                .Min();
            var nextPeriods = nextDueDate.HasValue
                ? openPeriods
                    .Where(period => period.DueDate == nextDueDate.Value)
                    .OrderBy(period => period.PeriodStart)
                    .ToList()
                : new List<RentPeriod>();
            var content = BuildEmailContent(
                tenancy,
                tenant,
                duePeriods,
                nextPeriods,
                category);
            var reminder = new RentReminder
            {
                TenancyId = tenancy.Id,
                Category = category,
                IsManual = isManual,
                ScheduledFor = scheduledFor,
                Status = RentReminderStatusEnum.Pending,
                OutstandingAmountSnapshot = duePeriods.Sum(period => Math.Max(0, period.Amount - period.PaidAmount)),
                IncludedPeriodCount = duePeriods.Count,
                RecipientEmail = tenant.Email?.Trim() ?? string.Empty,
                RecipientPhone = tenant.PhoneNumber?.Trim() ?? string.Empty,
                Subject = content.Subject,
                PlainTextBody = content.PlainText,
                HtmlBody = content.Html,
                EmailStatus = requestEmail && !string.IsNullOrWhiteSpace(tenant.Email)
                    ? ReminderDeliveryStatusEnum.Pending
                    : ReminderDeliveryStatusEnum.NotRequested,
                SmsStatus = requestSms && !string.IsNullOrWhiteSpace(tenant.PhoneNumber)
                    ? ReminderDeliveryStatusEnum.Pending
                    : ReminderDeliveryStatusEnum.NotRequested,
                RequestedByUserId = requestedByUserId,
                CreatedAt = nowUtc
            };

            foreach (var period in duePeriods)
            {
                reminder.Periods.Add(ToSnapshot(
                    period,
                    RentReminderPeriodRelationEnum.Outstanding,
                    triggerPeriodIds.Contains(period.Id)));
            }

            foreach (var nextPeriod in nextPeriods.Where(next =>
                         reminder.Periods.All(period => period.RentPeriodId != next.Id)))
            {
                reminder.Periods.Add(ToSnapshot(
                    nextPeriod,
                    RentReminderPeriodRelationEnum.UpcomingInformation,
                    triggerPeriodIds.Contains(nextPeriod.Id)));
            }

            return reminder;
        }

        private ReminderEmailContent BuildEmailContent(
            Tenancy tenancy,
            ApplicationUser tenant,
            IReadOnlyList<RentPeriod> duePeriods,
            IReadOnlyList<RentPeriod> nextPeriods,
            RentReminderCategoryEnum category)
        {
            var apartment = tenancy.Apartment!;
            var property = apartment.Property!;
            var language = tenant.EmailLanguage;
            var isFrench = language == PlatformLanguage.French;
            var culture = CultureInfo.GetCultureInfo(language.ToCultureName());
            var greetingName = string.IsNullOrWhiteSpace(tenant.FullName)
                ? (isFrench ? string.Empty : "there")
                : tenant.FullName.Trim();
            var totalDue = duePeriods.Sum(period => Math.Max(0, period.Amount - period.PaidAmount));
            var paymentUrl = BuildPortalUrl($"/Tenant/Tenancy?tenancyId={tenancy.Id}");
            var subject = BuildSubject(category, apartment.Name, duePeriods.Count, isFrench);

            var plain = new StringBuilder();
            plain.AppendLine(isFrench ? $"Bonjour {greetingName}," : $"Hello {greetingName},");
            plain.AppendLine();
            plain.AppendLine(BuildOpeningLine(category, duePeriods.Count, isFrench));
            plain.AppendLine(isFrench ? $"Propriété : {property.Name}" : $"Property: {property.Name}");
            plain.AppendLine(isFrench ? $"Appartement : {apartment.Name}" : $"Apartment: {apartment.Name}");
            plain.AppendLine(isFrench
                ? $"Fréquence de paiement : {PaymentFrequencyLabel(tenancy.PaymentIntervalMonths, true)}"
                : $"Payment frequency: {PaymentFrequencyLabel(tenancy.PaymentIntervalMonths, false)}");
            plain.AppendLine();

            if (duePeriods.Count > 0)
            {
                plain.AppendLine(isFrench ? "Périodes à payer :" : "Rent periods due:");
                foreach (var period in duePeriods)
                {
                    plain.AppendLine(
                        $"- {FormatPeriod(period, culture)} | {FormatDate(period.DueDate, culture)} | {FormatMoney(period.Amount - period.PaidAmount, culture)}");
                }

                plain.AppendLine(isFrench
                    ? $"Total exigible : {FormatMoney(totalDue, culture)}"
                    : $"Total due now: {FormatMoney(totalDue, culture)}");
            }

            if (nextPeriods.Count > 0)
            {
                plain.AppendLine();
                plain.AppendLine(isFrench ? "Prochain regroupement :" : "Next payment group:");
                foreach (var period in nextPeriods)
                {
                    plain.AppendLine($"- {FormatPeriod(period, culture)} | {FormatDate(period.DueDate, culture)} | {FormatMoney(period.Amount - period.PaidAmount, culture)}");
                }
                plain.AppendLine(isFrench
                    ? "Ce regroupement n’est pas compris dans le total exigible."
                    : "This group is not included in the total due now.");
            }

            plain.AppendLine();
            plain.AppendLine(isFrench
                ? "Les loyers sont affectés à la plus ancienne période impayée en premier."
                : "Rent payments are applied to the oldest unpaid period first.");
            plain.AppendLine(isFrench ? $"Ouvrir la location : {paymentUrl}" : $"Open tenancy: {paymentUrl}");
            plain.AppendLine();
            plain.AppendLine(isFrench ? "Merci," : "Thank you,");

            var html = BuildEmailHtml(
                category,
                duePeriods,
                nextPeriods,
                totalDue,
                property.Name,
                apartment.Name,
                greetingName,
                paymentUrl,
                culture,
                isFrench,
                tenancy.PaymentIntervalMonths);
            return new ReminderEmailContent(subject, plain.ToString().Trim(), html);
        }

        private static string BuildEmailHtml(
            RentReminderCategoryEnum category,
            IReadOnlyList<RentPeriod> duePeriods,
            IReadOnlyList<RentPeriod> nextPeriods,
            decimal totalDue,
            string propertyName,
            string apartmentName,
            string greetingName,
            string paymentUrl,
            CultureInfo culture,
            bool isFrench,
            int paymentIntervalMonths)
        {
            static string E(string value) => WebUtility.HtmlEncode(value);
            var rows = new StringBuilder();
            foreach (var period in duePeriods)
            {
                rows.Append("<tr>")
                    .Append($"<td style=\"padding:10px;border-bottom:1px solid #dde0d5;\">{E(FormatPeriod(period, culture))}</td>")
                    .Append($"<td style=\"padding:10px;border-bottom:1px solid #dde0d5;\">{E(FormatDate(period.DueDate, culture))}</td>")
                    .Append($"<td style=\"padding:10px;border-bottom:1px solid #dde0d5;text-align:right;white-space:nowrap;\">{E(FormatMoney(period.Amount, culture))}</td>")
                    .Append($"<td style=\"padding:10px;border-bottom:1px solid #dde0d5;text-align:right;white-space:nowrap;\">{E(FormatMoney(period.PaidAmount, culture))}</td>")
                    .Append($"<td style=\"padding:10px;border-bottom:1px solid #dde0d5;text-align:right;white-space:nowrap;font-weight:700;\">{E(FormatMoney(Math.Max(0, period.Amount - period.PaidAmount), culture))}</td>")
                    .Append("</tr>");
            }

            var table = duePeriods.Count == 0
                ? string.Empty
                : $$"""
                    <div style="overflow-x:auto;margin:20px 0;">
                      <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="width:100%;border-collapse:collapse;font-size:14px;">
                        <thead>
                          <tr style="background:#eef1dd;">
                            <th style="padding:10px;text-align:left;">{{(isFrench ? "Période" : "Period")}}</th>
                            <th style="padding:10px;text-align:left;">{{(isFrench ? "Échéance" : "Due date")}}</th>
                            <th style="padding:10px;text-align:right;">{{(isFrench ? "Montant" : "Amount")}}</th>
                            <th style="padding:10px;text-align:right;">{{(isFrench ? "Payé" : "Paid")}}</th>
                            <th style="padding:10px;text-align:right;">{{(isFrench ? "Reste" : "Balance")}}</th>
                          </tr>
                        </thead>
                        <tbody>{{rows}}</tbody>
                      </table>
                    </div>
                    <p style="margin:16px 0;font-size:18px;"><strong>{{(isFrench ? "Total exigible" : "Total due now")}} : {{E(FormatMoney(totalDue, culture))}}</strong></p>
                    """;

            var upcomingRows = new StringBuilder();
            foreach (var period in nextPeriods)
            {
                upcomingRows.Append("<div>")
                    .Append(E(FormatPeriod(period, culture)))
                    .Append(" · ")
                    .Append(E(FormatDate(period.DueDate, culture)))
                    .Append(" · ")
                    .Append(E(FormatMoney(period.Amount - period.PaidAmount, culture)))
                    .Append("</div>");
            }
            var upcoming = nextPeriods.Count == 0
                ? string.Empty
                : $$"""
                    <div style="margin:18px 0;padding:14px;background:#f3f4ed;border-left:4px solid #c9d45a;">
                      <strong>{{(isFrench ? "Prochain regroupement" : "Next payment group")}}</strong><br />
                      {{upcomingRows}}<br />
                      <span style="color:#61655a;">{{(isFrench ? "Non compris dans le total exigible." : "Not included in the total due now.")}}</span>
                    </div>
                    """;

            return $$"""
                <p>{{(isFrench ? $"Bonjour {E(greetingName)}," : $"Hello {E(greetingName)},")}}</p>
                <p>{{E(BuildOpeningLine(category, duePeriods.Count, isFrench))}}</p>
                <p><strong>{{E(propertyName)}} · {{E(apartmentName)}}</strong></p>
                <p>{{E(isFrench ? $"Fréquence de paiement : {PaymentFrequencyLabel(paymentIntervalMonths, true)}" : $"Payment frequency: {PaymentFrequencyLabel(paymentIntervalMonths, false)}")}}</p>
                {{table}}
                {{upcoming}}
                <p>{{(isFrench ? "Les loyers sont affectés à la plus ancienne période impayée en premier." : "Rent payments are applied to the oldest unpaid period first.")}}</p>
                <p><a href="{{E(paymentUrl)}}" style="display:inline-block;padding:11px 18px;background:#c9d45a;color:#20240f;text-decoration:none;border-radius:9px;font-weight:700;">{{(isFrench ? "Ouvrir la location" : "Open tenancy")}}</a></p>
                <p>{{(isFrench ? "Merci," : "Thank you,")}}</p>
                """;
        }

        private async Task<HashSet<string>> LoadExistingTriggerKeysAsync(IReadOnlyList<string> triggerKeys)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var batch in triggerKeys.Distinct(StringComparer.Ordinal).Chunk(TriggerKeyQueryBatchSize))
            {
                var keys = batch.ToList();
                var existing = await _context.RentReminderTriggers
                    .AsNoTracking()
                    .Where(trigger => keys.Contains(trigger.TriggerKey))
                    .Select(trigger => trigger.TriggerKey)
                    .ToListAsync();
                result.UnionWith(existing);
            }

            return result;
        }

        private static ApplicationUser? ResolvePrimaryTenant(Tenancy tenancy)
        {
            return tenancy.Members
                       .Where(member => !member.IsDeleted && member.Role == TenancyMemberRoleEnum.MainTenant)
                       .Select(member => member.Member)
                       .FirstOrDefault(member => member != null)
                   ?? tenancy.Members
                       .Where(member => !member.IsDeleted)
                       .Select(member => member.Member)
                       .FirstOrDefault(member => member != null);
        }

        private static RentReminderPeriod ToSnapshot(
            RentPeriod period,
            RentReminderPeriodRelationEnum relation,
            bool isTrigger)
        {
            return new RentReminderPeriod
            {
                RentPeriodId = period.Id,
                Relation = relation,
                IsTrigger = isTrigger,
                PeriodStartSnapshot = period.PeriodStart,
                PeriodEndSnapshot = period.PeriodEnd,
                DueDateSnapshot = period.DueDate,
                AmountSnapshot = period.Amount,
                PaidAmountSnapshot = period.PaidAmount,
                OutstandingAmountSnapshot = Math.Max(0, period.Amount - period.PaidAmount)
            };
        }

        private static RentReminderCategoryEnum ResolveBatchCategory(IEnumerable<RentReminderTimingEnum> timings)
        {
            var values = timings.ToHashSet();
            if (values.Contains(RentReminderTimingEnum.AfterDue)) return RentReminderCategoryEnum.AfterDue;
            if (values.Contains(RentReminderTimingEnum.OnDueDate)) return RentReminderCategoryEnum.DueDate;
            return RentReminderCategoryEnum.BeforeDue;
        }

        private static RentReminderCategoryEnum MapCategory(RentReminderTimingEnum timing)
        {
            return timing switch
            {
                RentReminderTimingEnum.BeforeDue => RentReminderCategoryEnum.BeforeDue,
                RentReminderTimingEnum.OnDueDate => RentReminderCategoryEnum.DueDate,
                RentReminderTimingEnum.AfterDue => RentReminderCategoryEnum.AfterDue,
                _ => RentReminderCategoryEnum.BeforeDue
            };
        }

        private static string BuildSubject(
            RentReminderCategoryEnum category,
            string apartmentName,
            int overdueCount,
            bool isFrench)
        {
            if (isFrench)
            {
                return category switch
                {
                    RentReminderCategoryEnum.AfterDue or RentReminderCategoryEnum.Manual when overdueCount > 1
                        => $"Loyers en retard – {overdueCount} périodes – {apartmentName}",
                    RentReminderCategoryEnum.AfterDue or RentReminderCategoryEnum.Manual
                        => $"Loyer en retard – {apartmentName}",
                    RentReminderCategoryEnum.DueDate => $"Loyer à payer aujourd’hui – {apartmentName}",
                    _ => $"Prochaine échéance de loyer – {apartmentName}"
                };
            }

            return category switch
            {
                RentReminderCategoryEnum.AfterDue or RentReminderCategoryEnum.Manual when overdueCount > 1
                    => $"Overdue rent – {overdueCount} periods – {apartmentName}",
                RentReminderCategoryEnum.AfterDue or RentReminderCategoryEnum.Manual
                    => $"Rent overdue – {apartmentName}",
                RentReminderCategoryEnum.DueDate => $"Rent due today – {apartmentName}",
                _ => $"Upcoming rent due date – {apartmentName}"
            };
        }

        private static string BuildOpeningLine(
            RentReminderCategoryEnum category,
            int overdueCount,
            bool isFrench)
        {
            if (isFrench)
            {
                return category switch
                {
                    RentReminderCategoryEnum.Manual => "Voici un rappel concernant le loyer actuellement dû.",
                    RentReminderCategoryEnum.AfterDue when overdueCount > 1 => $"{overdueCount} périodes de loyer restent impayées.",
                    RentReminderCategoryEnum.AfterDue => "Une période de loyer reste impayée après son échéance.",
                    RentReminderCategoryEnum.DueDate => "Votre loyer arrive à échéance aujourd’hui.",
                    _ => overdueCount > 0
                        ? "Votre prochaine échéance approche et un solde antérieur reste dû."
                        : "Votre prochaine échéance de loyer approche."
                };
            }

            return category switch
            {
                RentReminderCategoryEnum.Manual => "This is a reminder about rent that is currently due.",
                RentReminderCategoryEnum.AfterDue when overdueCount > 1 => $"{overdueCount} rent periods remain unpaid.",
                RentReminderCategoryEnum.AfterDue => "A rent period remains unpaid after its due date.",
                RentReminderCategoryEnum.DueDate => "Your rent is due today.",
                _ => overdueCount > 0
                    ? "Your next rent date is approaching and an earlier balance remains due."
                    : "Your next rent due date is approaching."
            };
        }

        private static string BuildSms(RentReminder reminder)
        {
            return $"Lontsi Homes: rent reminder. Due now: {reminder.OutstandingAmountSnapshot:N0} {Currency}.";
        }

        private string BuildPortalUrl(string path)
        {
            var portalBaseUrl = (_configuration["Portal:BaseUrl"] ?? "https://localhost:7059")
                .Trim()
                .TrimEnd('/');
            return $"{portalBaseUrl}{path}";
        }

        private static string FormatPeriod(RentPeriod period, CultureInfo culture)
            => $"{FormatDate(period.PeriodStart, culture)} – {FormatDate(period.PeriodEnd, culture)}";

        private static string FormatDate(DateTimeOffset value, CultureInfo culture)
            => value.ToString("dd MMM yyyy", culture);

        private static string FormatMoney(decimal value, CultureInfo culture)
            => $"{Math.Max(0, value).ToString("N0", culture)} {Currency}";

        private static string PaymentFrequencyLabel(int months, bool isFrench)
        {
            var interval = Math.Clamp(months, 1, 12);
            if (isFrench)
            {
                return interval switch
                {
                    1 => "mensuelle",
                    2 => "tous les 2 mois",
                    3 => "trimestrielle",
                    6 => "semestrielle",
                    12 => "annuelle",
                    _ => $"tous les {interval} mois"
                };
            }

            return interval switch
            {
                1 => "monthly",
                3 => "quarterly",
                6 => "semiannual",
                12 => "annual",
                _ => $"every {interval} months"
            };
        }

        private static bool IsUniqueConstraintViolation(DbUpdateException exception)
        {
            return exception.InnerException is SqlException sqlException
                   && sqlException.Number is 2601 or 2627;
        }

        private sealed record AutomaticReminderCandidate(
            int TenancyId,
            int RuleId,
            int RentPeriodId,
            RentReminderTimingEnum Timing,
            int Days,
            bool EmailEnabled,
            bool SmsEnabled,
            DateTimeOffset ScheduledFor,
            DateTimeOffset DueDate,
            DateTimeOffset ScheduleVersion,
            string TriggerKey);

        private sealed record ReminderEmailContent(string Subject, string PlainText, string Html);
    }

    internal static class RentReminderStringExtensions
    {
        public static string Truncate(this string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            return value[..maxLength];
        }
    }
}
