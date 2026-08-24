using System.Data;
using Common.CommunicationModels;
using Common.Enums;
using Common.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Helpers;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Permissions;
using RentHub.API.Services.Tenancies;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/tenancies/{tenancyId}/termination-requests")]
    public class TenancyTerminationRequestsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IManagerPermissionService _permissionService;
        private readonly ITenancyTerminationEmailService _emailService;
        private readonly ILogger<TenancyTerminationRequestsController> _logger;

        public TenancyTerminationRequestsController(
            ApplicationDbContext context,
            IManagerPermissionService permissionService,
            ITenancyTerminationEmailService emailService,
            ILogger<TenancyTerminationRequestsController> logger)
        {
            _context = context;
            _permissionService = permissionService;
            _emailService = emailService;
            _logger = logger;
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetRequests(int tenancyId, CancellationToken cancellationToken)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            var tenancy = await LoadTenancyAsync(tenancyId, cancellationToken);
            if (tenancy?.Apartment?.Property == null) return NotFound("Tenancy not found.");
            if (!await PropertyHelpers.CanAccessTenancyAsync(
                    _context,
                    tenancy.Id,
                    tenancy.ApartmentId,
                    tenancy.Apartment.PropertyId,
                    userId,
                    User.IsInRole("Admin")))
            {
                return Forbid();
            }

            var requestEntities = await _context.TenancyTerminationRequests
                .AsNoTracking()
                .Include(request => request.RequestedBy)
                .Include(request => request.ReviewedBy)
                .Where(request => request.TenancyId == tenancyId)
                .OrderByDescending(request => request.CreatedAt)
                .ToListAsync(cancellationToken);
            var requests = requestEntities.Select(MapRequest).ToList();
            return Ok(requests);
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> CreateRequest(
            int tenancyId,
            [FromBody] CreateTenancyTerminationRequest input,
            CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            var tenancy = await LoadTenancyAsync(tenancyId, cancellationToken);
            if (tenancy?.Apartment?.Property == null) return NotFound("Tenancy not found.");

            var membership = await _context.TenancyMembers
                .Include(member => member.Member)
                .FirstOrDefaultAsync(member =>
                    member.TenancyId == tenancyId &&
                    !member.IsDeleted &&
                    member.MemberId == userId,
                    cancellationToken);
            if (membership?.Role is not (TenancyMemberRoleEnum.MainTenant or TenancyMemberRoleEnum.CoTenant))
                return Forbid();

            var requestedEndDate = new DateTimeOffset(
                input.RequestedEndDate.Year,
                input.RequestedEndDate.Month,
                input.RequestedEndDate.Day,
                0,
                0,
                0,
                TimeSpan.Zero);
            var validationError = ValidateRequestedEndDate(tenancy, requestedEndDate, DateTimeOffset.UtcNow);
            if (!string.IsNullOrEmpty(validationError)) return BadRequest(new { Message = validationError });

            var hasPendingRequest = await _context.TenancyTerminationRequests.AnyAsync(request =>
                request.TenancyId == tenancyId &&
                request.Status == TenancyTerminationRequestStatusEnum.Pending,
                cancellationToken);
            if (hasPendingRequest)
                return Conflict(new { Message = "A tenancy end request is already pending for this tenancy." });
            if (await _context.TenancyExtensionRequests.AnyAsync(request =>
                    request.TenancyId == tenancyId &&
                    request.Status == TenancyExtensionStatusEnum.Pending,
                    cancellationToken))
            {
                return Conflict(new { Message = "Review or cancel the pending renewal request before requesting the end of this tenancy." });
            }

            var nowUtc = DateTimeOffset.UtcNow;
            var request = new TenancyTerminationRequest
            {
                TenancyId = tenancyId,
                Tenancy = tenancy,
                RequestedById = userId,
                RequestedBy = membership.Member,
                OriginalEndDate = tenancy.EndDate,
                RequestedEndDate = requestedEndDate,
                Reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim(),
                Status = TenancyTerminationRequestStatusEnum.Pending,
                CreatedBy = userId,
                CreatedAt = nowUtc
            };

            try
            {
                _context.TenancyTerminationRequests.Add(request);
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Duplicate termination request blocked for tenancy {TenancyId}.", tenancyId);
                return Conflict(new { Message = "A tenancy end request is already pending for this tenancy." });
            }

            await TrySendSubmissionEmailAsync(request, cancellationToken);
            return Ok(new { Message = "Tenancy end request submitted.", RequestId = request.Id });
        }

        [HttpPut("{requestId}/approve")]
        [Authorize]
        public async Task<IActionResult> ApproveRequest(
            int tenancyId,
            int requestId,
            CancellationToken cancellationToken)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            TenancyTerminationRequest? request;
            await using (var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken))
            {
                request = await LoadRequestForDecisionAsync(tenancyId, requestId, cancellationToken);
                if (request == null) return NotFound("Tenancy end request not found.");
                if (request.Status != TenancyTerminationRequestStatusEnum.Pending)
                    return Conflict(new { Message = "This tenancy end request has already been reviewed." });

                var canReview = await _permissionService.HasTenancyPermissionAsync(
                    userId,
                    tenancyId,
                    ManagerPermission.TerminateTenancy,
                    User.IsInRole("Admin"));
                if (!canReview) return Forbid();

                var tenancy = request.Tenancy!;
                if (await _context.TenancyExtensionRequests.AnyAsync(extension =>
                        extension.TenancyId == tenancyId &&
                        extension.Status == TenancyExtensionStatusEnum.Pending,
                        cancellationToken))
                {
                    return Conflict(new { Message = "The pending renewal request must be reviewed before this tenancy end request." });
                }
                var validationError = ValidateRequestedEndDate(tenancy, request.RequestedEndDate, DateTimeOffset.UtcNow);
                if (!string.IsNullOrEmpty(validationError)) return Conflict(new { Message = validationError });

                var nowUtc = DateTimeOffset.UtcNow;
                await TenancyLifecycleHelper.ApplyApprovedTerminationAsync(
                    _context,
                    tenancy,
                    request.RequestedEndDate,
                    userId,
                    request.Reason,
                    nowUtc,
                    cancellationToken);
                request.Status = TenancyTerminationRequestStatusEnum.Approved;
                request.ReviewedById = userId;
                request.ReviewedAt = nowUtc;
                request.RejectionReason = null;
                request.UpdatedBy = userId;
                request.UpdatedAt = nowUtc;

                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            await TrySendDecisionEmailAsync(request, cancellationToken);
            return Ok(new
            {
                Message = "Tenancy end request approved.",
                TenancyId = tenancyId,
                EndDate = request.RequestedEndDate
            });
        }

        [HttpPut("{requestId}/reject")]
        [Authorize]
        public async Task<IActionResult> RejectRequest(
            int tenancyId,
            int requestId,
            [FromBody] RejectTenancyTerminationRequest decision,
            CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);
            if (string.IsNullOrWhiteSpace(decision.Reason))
                return BadRequest(new { Message = "A rejection reason is required." });

            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            TenancyTerminationRequest? request;
            await using (var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken))
            {
                request = await LoadRequestForDecisionAsync(tenancyId, requestId, cancellationToken);
                if (request == null) return NotFound("Tenancy end request not found.");
                if (request.Status != TenancyTerminationRequestStatusEnum.Pending)
                    return Conflict(new { Message = "This tenancy end request has already been reviewed." });

                var canReview = await _permissionService.HasTenancyPermissionAsync(
                    userId,
                    tenancyId,
                    ManagerPermission.TerminateTenancy,
                    User.IsInRole("Admin"));
                if (!canReview) return Forbid();

                var nowUtc = DateTimeOffset.UtcNow;
                request.Status = TenancyTerminationRequestStatusEnum.Rejected;
                request.ReviewedById = userId;
                request.ReviewedAt = nowUtc;
                request.RejectionReason = decision.Reason.Trim();
                request.UpdatedBy = userId;
                request.UpdatedAt = nowUtc;
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            await TrySendDecisionEmailAsync(request, cancellationToken);
            return Ok(new { Message = "Tenancy end request rejected." });
        }

        [HttpPut("{requestId}/withdraw")]
        [Authorize]
        public async Task<IActionResult> WithdrawPendingRequest(
            int tenancyId,
            int requestId,
            CancellationToken cancellationToken)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            TenancyTerminationRequest? request;
            await using (var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken))
            {
                request = await LoadRequestForDecisionAsync(tenancyId, requestId, cancellationToken);
                if (request == null) return NotFound("Tenancy end request not found.");
                if (!string.Equals(request.RequestedById, userId, StringComparison.Ordinal)) return Forbid();
                if (request.Status != TenancyTerminationRequestStatusEnum.Pending)
                {
                    return Conflict(new { Message = "Only a tenancy end request awaiting a decision can be cancelled." });
                }

                var nowUtc = DateTimeOffset.UtcNow;
                request.Status = TenancyTerminationRequestStatusEnum.Cancelled;
                request.UpdatedBy = userId;
                request.UpdatedAt = nowUtc;
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            await TrySendWithdrawalEmailAsync(request, cancellationToken);
            return Ok(new { Message = "Tenancy end request cancelled." });
        }

        [HttpPut("{requestId}/cancel")]
        [Authorize]
        public async Task<IActionResult> CancelApprovedDecision(
            int tenancyId,
            int requestId,
            [FromBody] CancelTenancyTerminationRequest input,
            CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);
            if (!Enum.IsDefined(input.Mode))
                return BadRequest(new { Message = "Choose how the tenancy should continue." });

            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            TenancyTerminationRequest? request;
            int? replacementTenancyId = null;
            await using (var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken))
            {
                request = await LoadRequestForDecisionAsync(tenancyId, requestId, cancellationToken);
                if (request == null) return NotFound("Tenancy end decision not found.");
                if (request.Status != TenancyTerminationRequestStatusEnum.Approved)
                    return Conflict(new { Message = "Only an approved tenancy end decision can be cancelled." });

                var canCancel = await _permissionService.HasTenancyPermissionAsync(
                    userId,
                    tenancyId,
                    ManagerPermission.TerminateTenancy,
                    User.IsInRole("Admin"));
                if (!canCancel) return Forbid();

                var tenancy = request.Tenancy!;
                if (!tenancy.TerminatedAt.HasValue)
                    return Conflict(new { Message = "This tenancy no longer has an active termination decision." });

                var nowUtc = DateTimeOffset.UtcNow;
                DateTimeOffset? normalizedEndDate = input.NewEndDate.HasValue
                    ? CalendarDate(input.NewEndDate.Value)
                    : null;
                if (normalizedEndDate.HasValue && normalizedEndDate.Value.Date <= request.RequestedEndDate.Date)
                {
                    return BadRequest(new { Message = "The new fixed end date must be after the cancelled termination date." });
                }

                string cancellationSummary;
                if (input.Mode == TenancyTerminationCancellationModeEnum.ContinueCurrentTenancy)
                {
                    if (await TenancyLifecycleHelper.HasOverlappingTenancyAsync(
                            _context,
                            tenancy.ApartmentId,
                            tenancy.StartDate,
                            normalizedEndDate,
                            tenancy.Id,
                            cancellationToken))
                    {
                        return Conflict(new { Message = "The current tenancy cannot be restored because another tenancy overlaps the selected period." });
                    }

                    await TenancyLifecycleHelper.RestoreCurrentTenancyAsync(
                        _context,
                        tenancy,
                        normalizedEndDate,
                        userId,
                        nowUtc,
                        cancellationToken);
                    cancellationSummary = normalizedEndDate.HasValue
                        ? $"The current tenancy was restored and now ends on {normalizedEndDate.Value:yyyy-MM-dd}."
                        : "The current tenancy was restored without a fixed end date.";
                }
                else
                {
                    if (!input.ReplacementStartDate.HasValue)
                        return BadRequest(new { Message = "Choose the start date of the replacement tenancy." });

                    var replacementStartDate = CalendarDate(input.ReplacementStartDate.Value);
                    if (replacementStartDate.Date <= request.RequestedEndDate.Date)
                        return BadRequest(new { Message = "The replacement tenancy must start after the cancelled tenancy end date." });
                    if (normalizedEndDate.HasValue && normalizedEndDate.Value.Date < replacementStartDate.Date)
                        return BadRequest(new { Message = "The replacement tenancy end date cannot be before its start date." });
                    if (await TenancyLifecycleHelper.HasOverlappingTenancyAsync(
                            _context,
                            tenancy.ApartmentId,
                            replacementStartDate,
                            normalizedEndDate,
                            tenancy.Id,
                            cancellationToken))
                    {
                        return Conflict(new { Message = "Another tenancy overlaps the selected replacement period." });
                    }

                    var replacement = new Tenancy
                    {
                        ApartmentId = tenancy.ApartmentId,
                        StartDate = replacementStartDate,
                        EndDate = normalizedEndDate,
                        MonthlyRent = tenancy.MonthlyRent,
                        MaxMembers = tenancy.MaxMembers,
                        RentDueDay = tenancy.RentDueDay,
                        PaymentIntervalMonths = tenancy.PaymentIntervalMonths,
                        EndBehavior = normalizedEndDate.HasValue
                            ? TenancyEndBehaviorEnum.ExpireAutomatically
                            : TenancyEndBehaviorEnum.NoEndDate,
                        FutureRentPeriodCount = tenancy.FutureRentPeriodCount,
                        RentTrackingStartDate = replacementStartDate,
                        CreatedBy = userId,
                        CreatedAt = nowUtc
                    };
                    _context.Tenancies.Add(replacement);
                    await _context.SaveChangesAsync(cancellationToken);
                    replacementTenancyId = replacement.Id;

                    foreach (var member in tenancy.Members.Where(member => !member.IsDeleted))
                    {
                        _context.TenancyMembers.Add(new TenancyMember
                        {
                            TenancyId = replacement.Id,
                            MemberId = member.MemberId,
                            Role = member.Role,
                            CreatedBy = userId,
                            CreatedAt = nowUtc
                        });
                    }
                    foreach (var period in RentPeriodScheduleHelper.GeneratePeriods(
                                 replacementStartDate,
                                 normalizedEndDate,
                                 replacement.EndBehavior,
                                 replacement.MonthlyRent,
                                 replacement.RentDueDay,
                                 nowUtc,
                                 replacement.FutureRentPeriodCount,
                                 replacement.PaymentIntervalMonths,
                                 replacement.RentTrackingStartDate))
                    {
                        _context.RentPeriods.Add(new RentPeriod
                        {
                            TenancyId = replacement.Id,
                            PeriodStart = period.PeriodStart,
                            PeriodEnd = period.PeriodEnd,
                            DueDate = period.DueDate,
                            BillingGroupSequence = period.BillingGroupSequence,
                            Amount = period.Amount,
                            PaidAmount = 0,
                            Status = period.Status,
                            CreatedBy = userId,
                            CreatedAt = nowUtc
                        });
                    }

                    cancellationSummary = normalizedEndDate.HasValue
                        ? $"A replacement tenancy #{replacement.Id} starts on {replacementStartDate:yyyy-MM-dd} and ends on {normalizedEndDate.Value:yyyy-MM-dd}."
                        : $"A replacement tenancy #{replacement.Id} starts on {replacementStartDate:yyyy-MM-dd} without a fixed end date.";
                }

                if (!string.IsNullOrWhiteSpace(input.Note))
                {
                    cancellationSummary = $"{cancellationSummary} {input.Note.Trim()}";
                }

                request.Status = TenancyTerminationRequestStatusEnum.Cancelled;
                request.RejectionReason = cancellationSummary.Length > 1000
                    ? cancellationSummary[..1000]
                    : cancellationSummary;
                request.UpdatedBy = userId;
                request.UpdatedAt = nowUtc;
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            try
            {
                await _emailService.SendCancellationAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Termination decision {RequestId} was cancelled but its email notification failed.", requestId);
            }

            return Ok(new
            {
                Message = "Tenancy termination decision cancelled.",
                TenancyId = tenancyId,
                ReplacementTenancyId = replacementTenancyId
            });
        }

        private Task<Tenancy?> LoadTenancyAsync(int tenancyId, CancellationToken cancellationToken)
        {
            return _context.Tenancies
                .Include(tenancy => tenancy.Apartment)
                .ThenInclude(apartment => apartment!.Property)
                .ThenInclude(property => property!.Landlord)
                .FirstOrDefaultAsync(tenancy => tenancy.Id == tenancyId, cancellationToken);
        }

        private Task<TenancyTerminationRequest?> LoadRequestForDecisionAsync(
            int tenancyId,
            int requestId,
            CancellationToken cancellationToken)
        {
            return _context.TenancyTerminationRequests
                .Include(request => request.RequestedBy)
                .Include(request => request.Tenancy)
                .ThenInclude(tenancy => tenancy!.Members)
                .Include(request => request.Tenancy)
                .ThenInclude(tenancy => tenancy!.Apartment)
                .ThenInclude(apartment => apartment!.Property)
                .ThenInclude(property => property!.Landlord)
                .FirstOrDefaultAsync(request =>
                    request.Id == requestId && request.TenancyId == tenancyId,
                    cancellationToken);
        }

        private static string ValidateRequestedEndDate(
            Tenancy tenancy,
            DateTimeOffset requestedEndDate,
            DateTimeOffset nowUtc)
        {
            if (tenancy.TerminatedAt.HasValue) return "This tenancy already has an approved end date.";
            if (requestedEndDate.Date < nowUtc.Date) return "The requested end date cannot be in the past.";
            if (requestedEndDate.Date < tenancy.StartDate.Date) return "The requested end date cannot be before the tenancy start date.";
            if (tenancy.EndDate.HasValue && requestedEndDate.Date >= tenancy.EndDate.Value.Date)
                return "For a fixed-term tenancy, the requested end date must be earlier than the current end date.";
            return string.Empty;
        }

        private async Task TrySendSubmissionEmailAsync(
            TenancyTerminationRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                await _emailService.SendRequestSubmittedAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Termination request {RequestId} was saved but its notification email failed.", request.Id);
            }
        }

        private async Task TrySendDecisionEmailAsync(
            TenancyTerminationRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                await _emailService.SendDecisionAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Termination request {RequestId} was decided but its notification email failed.", request.Id);
            }
        }

        private async Task TrySendWithdrawalEmailAsync(
            TenancyTerminationRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                await _emailService.SendRequestWithdrawnAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Termination request {RequestId} was cancelled but its notification email failed.", request.Id);
            }
        }

        private static TenancyTerminationRequestDto MapRequest(TenancyTerminationRequest request)
        {
            return new TenancyTerminationRequestDto
            {
                Id = request.Id,
                TenancyId = request.TenancyId,
                RequestedById = request.RequestedById,
                RequestedByName = request.RequestedBy?.FullName ?? request.RequestedBy?.Email ?? "Tenant",
                OriginalEndDate = request.OriginalEndDate,
                RequestedEndDate = request.RequestedEndDate,
                Reason = request.Reason,
                Status = request.Status,
                ReviewedByName = request.ReviewedBy?.FullName ?? request.ReviewedBy?.Email,
                ReviewedAt = request.ReviewedAt,
                RejectionReason = request.RejectionReason,
                CreatedAt = request.CreatedAt,
                IsDirectDecision = string.Equals(request.RequestedById, request.ReviewedById, StringComparison.Ordinal)
            };
        }

        private static DateTimeOffset CalendarDate(DateTimeOffset value)
            => new(value.Year, value.Month, value.Day, 0, 0, 0, TimeSpan.Zero);
    }
}
