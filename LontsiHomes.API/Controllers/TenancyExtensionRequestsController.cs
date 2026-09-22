using System.Data;
using Common.CommunicationModels;
using Common.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LontsiHomes.API.Data;
using LontsiHomes.API.Helpers;
using LontsiHomes.API.Models.Entities;
using LontsiHomes.API.Services.Permissions;
using LontsiHomes.API.Services.Tenancies;

namespace LontsiHomes.API.Controllers
{
    [ApiController]
    [Route("api/tenancies/{tenancyId}/extension-requests")]
    public class TenancyExtensionRequestsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IManagerPermissionService _permissionService;
        private readonly ITenancyRenewalEmailService _renewalEmailService;
        private readonly ILogger<TenancyExtensionRequestsController> _logger;

        public TenancyExtensionRequestsController(
            ApplicationDbContext context,
            IManagerPermissionService permissionService,
            ITenancyRenewalEmailService renewalEmailService,
            ILogger<TenancyExtensionRequestsController> logger)
        {
            _context = context;
            _permissionService = permissionService;
            _renewalEmailService = renewalEmailService;
            _logger = logger;
        }

        [HttpGet]
        [Authorize]
        public async Task<IActionResult> GetRequests(int tenancyId, CancellationToken cancellationToken)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            var tenancy = await _context.Tenancies
                .AsNoTracking()
                .Include(entity => entity.Apartment)
                .ThenInclude(apartment => apartment!.Property)
                .FirstOrDefaultAsync(entity => entity.Id == tenancyId, cancellationToken);
            if (tenancy?.Apartment?.Property == null) return NotFound("Tenancy not found.");

            var isAdmin = User.IsInRole("Admin");
            var canAccess = await PropertyHelpers.CanAccessTenancyAsync(
                _context, tenancy.Id, tenancy.ApartmentId, tenancy.Apartment.PropertyId, userId, isAdmin);
            if (!canAccess) return Forbid();

            var membership = await _context.TenancyMembers
                .AsNoTracking()
                .FirstOrDefaultAsync(member =>
                    member.TenancyId == tenancyId && !member.IsDeleted && member.MemberId == userId,
                    cancellationToken);
            var isRequestingTenant = membership?.Role is TenancyMemberRoleEnum.MainTenant or TenancyMemberRoleEnum.CoTenant;
            var canReview = await _permissionService.HasTenancyPermissionAsync(
                userId, tenancy.Id, ManagerPermission.RenewTenancy, isAdmin);
            var requests = await _context.TenancyExtensionRequests
                .AsNoTracking()
                .Include(request => request.RequestedBy)
                .Include(request => request.ApprovedBy)
                .Where(request => request.TenancyId == tenancyId)
                .OrderByDescending(request => request.CreatedAt)
                .ToListAsync(cancellationToken);

            var nowUtc = DateTimeOffset.UtcNow;
            var hasPendingRequest = requests.Any(request => request.Status == TenancyExtensionStatusEnum.Pending);
            var hasPendingTerminationRequest = await _context.TenancyTerminationRequests
                .AsNoTracking()
                .AnyAsync(request =>
                    request.TenancyId == tenancyId &&
                    request.Status == TenancyTerminationRequestStatusEnum.Pending,
                    cancellationToken);
            var unavailableReason = ResolveRequestUnavailableReason(
                tenancy,
                isRequestingTenant,
                hasPendingRequest,
                hasPendingTerminationRequest,
                nowUtc);

            return Ok(new TenancyRenewalWorkspaceDto
            {
                TenancyId = tenancy.Id,
                PropertyName = tenancy.Apartment.Property.Name,
                ApartmentName = tenancy.Apartment.Name,
                StartDate = tenancy.StartDate,
                CurrentEndDate = tenancy.EndDate,
                IsTerminated = tenancy.TerminatedAt.HasValue,
                IsExpired = tenancy.EndDate.HasValue && tenancy.EndDate.Value.Date < nowUtc.Date,
                CanRequest = string.IsNullOrEmpty(unavailableReason),
                CanReview = canReview,
                RequestUnavailableReason = unavailableReason,
                MinimumProposedEndDate = tenancy.EndDate?.Date.AddDays(1),
                Requests = requests.Select(MapRequest).ToList()
            });
        }

        [HttpPost]
        [Authorize]
        public async Task<IActionResult> CreateRequest(
            int tenancyId,
            [FromBody] ExtendTenancyRequest request,
            CancellationToken cancellationToken)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            var tenancy = await _context.Tenancies
                .Include(entity => entity.Apartment)
                .ThenInclude(apartment => apartment!.Property)
                .FirstOrDefaultAsync(entity => entity.Id == tenancyId, cancellationToken);
            if (tenancy?.Apartment?.Property == null) return NotFound("Tenancy not found.");

            var membership = await _context.TenancyMembers
                .Include(member => member.Member)
                .FirstOrDefaultAsync(member =>
                    member.TenancyId == tenancyId && !member.IsDeleted && member.MemberId == userId,
                    cancellationToken);
            if (membership?.Role is not (TenancyMemberRoleEnum.MainTenant or TenancyMemberRoleEnum.CoTenant))
                return Forbid();

            var nowUtc = DateTimeOffset.UtcNow;
            var hasPendingRequest = await _context.TenancyExtensionRequests.AnyAsync(existing =>
                existing.TenancyId == tenancyId && existing.Status == TenancyExtensionStatusEnum.Pending,
                cancellationToken);
            var unavailableReason = ResolveRequestUnavailableReason(tenancy, true, hasPendingRequest, false, nowUtc);
            if (!string.IsNullOrEmpty(unavailableReason))
                return Conflict(new { Message = unavailableReason });
            if (await _context.TenancyTerminationRequests.AnyAsync(existing =>
                    existing.TenancyId == tenancyId &&
                    existing.Status == TenancyTerminationRequestStatusEnum.Pending,
                    cancellationToken))
            {
                return Conflict(new { Message = "Review or cancel the pending tenancy end request before requesting a renewal." });
            }

            if (!tenancy.EndDate.HasValue)
                return BadRequest(new { Message = "A tenancy without an end date cannot be renewed." });
            if (request.NewEndDate.Date <= tenancy.EndDate.Value.Date)
                return BadRequest(new { Message = "The proposed end date must be later than the current tenancy end date." });
            if (request.NewEndDate.Date <= nowUtc.Date)
                return BadRequest(new { Message = "The proposed end date must be in the future." });

            var extensionRequest = new TenancyExtensionRequest
            {
                TenancyId = tenancyId,
                Tenancy = tenancy,
                RequestedById = userId,
                RequestedBy = membership.Member,
                OriginalEndDate = tenancy.EndDate,
                ProposedEndDate = request.NewEndDate.Date,
                Status = TenancyExtensionStatusEnum.Pending,
                CreatedBy = userId,
                CreatedAt = nowUtc,
                IsDeleted = false
            };

            try
            {
                _context.TenancyExtensionRequests.Add(extensionRequest);
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Duplicate tenancy renewal request blocked for tenancy {TenancyId}.", tenancyId);
                return Conflict(new { Message = "A renewal request is already pending for this tenancy." });
            }

            await TrySendSubmissionEmailAsync(extensionRequest, cancellationToken);
            return Ok(new { Message = "Renewal request submitted.", RequestId = extensionRequest.Id });
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

            await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            try
            {
                var request = await LoadRequestForDecisionAsync(tenancyId, requestId, cancellationToken);
                if (request == null) return NotFound("Renewal request not found.");
                if (request.Status != TenancyExtensionStatusEnum.Pending)
                    return Conflict(new { Message = "This renewal request has already been reviewed." });

                var tenancy = request.Tenancy!;
                if (await _context.TenancyTerminationRequests.AnyAsync(existing =>
                        existing.TenancyId == tenancyId &&
                        existing.Status == TenancyTerminationRequestStatusEnum.Pending,
                        cancellationToken))
                {
                    return Conflict(new { Message = "The pending tenancy end request must be reviewed before this renewal request." });
                }
                var canWrite = await _permissionService.HasTenancyPermissionAsync(
                    userId, tenancy.Id, ManagerPermission.RenewTenancy, User.IsInRole("Admin"));
                if (!canWrite) return Forbid();

                var nowUtc = DateTimeOffset.UtcNow;
                if (tenancy.TerminatedAt.HasValue)
                    return Conflict(new { Message = "A terminated tenancy cannot be renewed." });
                if (!tenancy.EndDate.HasValue)
                    return Conflict(new { Message = "A tenancy without an end date cannot be renewed." });
                if (tenancy.EndDate.Value.Date < nowUtc.Date)
                    return Conflict(new { Message = "An expired tenancy cannot be renewed." });
                if (request.ProposedEndDate.Date <= tenancy.EndDate.Value.Date)
                    return Conflict(new { Message = "The proposed end date is no longer later than the current tenancy end date." });
                if (request.ProposedEndDate.Date <= nowUtc.Date)
                    return Conflict(new { Message = "The proposed end date is no longer in the future." });

                var overlaps = await TenancyLifecycleHelper.HasOverlappingTenancyAsync(
                    _context, tenancy.ApartmentId, tenancy.StartDate, request.ProposedEndDate, tenancy.Id, cancellationToken);
                if (overlaps)
                    return Conflict(new { Message = "The renewal would overlap another tenancy for this apartment." });

                await TenancyLifecycleHelper.SynchronizeRentPeriodsForExtensionAsync(
                    _context, tenancy, request.ProposedEndDate, userId, nowUtc, cancellationToken);

                tenancy.EndDate = request.ProposedEndDate;
                tenancy.RenewalReminderSentAt = null;
                tenancy.RenewalReminderSentForEndDate = null;
                tenancy.UpdatedBy = userId;
                tenancy.UpdatedAt = nowUtc;
                request.Status = TenancyExtensionStatusEnum.Approved;
                request.ApprovedById = userId;
                request.ApprovedAt = nowUtc;
                request.RejectionReason = null;
                request.UpdatedBy = userId;
                request.UpdatedAt = nowUtc;

                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                await TrySendDecisionEmailAsync(request, cancellationToken);
                return Ok(new { Message = "Renewal request approved.", TenancyId = tenancy.Id, NewEndDate = tenancy.EndDate });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogError(ex, "Failed to approve renewal request {RequestId} for tenancy {TenancyId}.", requestId, tenancyId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "Unable to approve the renewal request right now." });
            }
        }

        [HttpPut("{requestId}/reject")]
        [Authorize]
        public async Task<IActionResult> RejectRequest(
            int tenancyId,
            int requestId,
            [FromBody] RejectTenancyExtensionRequest decision,
            CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid) return ValidationProblem(ModelState);

            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            try
            {
                var request = await LoadRequestForDecisionAsync(tenancyId, requestId, cancellationToken);
                if (request == null) return NotFound("Renewal request not found.");
                if (request.Status != TenancyExtensionStatusEnum.Pending)
                    return Conflict(new { Message = "This renewal request has already been reviewed." });

                var tenancy = request.Tenancy!;
                var canWrite = await _permissionService.HasTenancyPermissionAsync(
                    userId, tenancy.Id, ManagerPermission.RenewTenancy, User.IsInRole("Admin"));
                if (!canWrite) return Forbid();

                var nowUtc = DateTimeOffset.UtcNow;
                request.Status = TenancyExtensionStatusEnum.Rejected;
                request.ApprovedById = userId;
                request.ApprovedAt = nowUtc;
                if (string.IsNullOrWhiteSpace(decision.Reason))
                    return BadRequest(new { Message = "A rejection reason is required." });
                request.RejectionReason = decision.Reason.Trim();
                request.UpdatedBy = userId;
                request.UpdatedAt = nowUtc;

                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                await TrySendDecisionEmailAsync(request, cancellationToken);
                return Ok(new { Message = "Renewal request rejected." });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogError(ex, "Failed to reject renewal request {RequestId} for tenancy {TenancyId}.", requestId, tenancyId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "Unable to reject the renewal request right now." });
            }
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

            await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            try
            {
                var request = await LoadRequestForDecisionAsync(tenancyId, requestId, cancellationToken);
                if (request == null) return NotFound("Renewal request not found.");
                if (!string.Equals(request.RequestedById, userId, StringComparison.Ordinal)) return Forbid();
                if (request.Status != TenancyExtensionStatusEnum.Pending)
                {
                    return Conflict(new { Message = "Only a renewal request awaiting a decision can be cancelled." });
                }

                var nowUtc = DateTimeOffset.UtcNow;
                request.Status = TenancyExtensionStatusEnum.Cancelled;
                request.UpdatedBy = userId;
                request.UpdatedAt = nowUtc;
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                await TrySendWithdrawalEmailAsync(request, cancellationToken);
                return Ok(new { Message = "Renewal request cancelled." });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogError(ex, "Failed to cancel renewal request {RequestId} for tenancy {TenancyId}.", requestId, tenancyId);
                return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "Unable to cancel the renewal request right now." });
            }
        }

        private Task<TenancyExtensionRequest?> LoadRequestForDecisionAsync(
            int tenancyId,
            int requestId,
            CancellationToken cancellationToken)
        {
            return _context.TenancyExtensionRequests
                .Include(request => request.RequestedBy)
                .Include(request => request.Tenancy)
                .ThenInclude(tenancy => tenancy!.Apartment)
                .ThenInclude(apartment => apartment!.Property)
                .FirstOrDefaultAsync(request => request.Id == requestId && request.TenancyId == tenancyId, cancellationToken);
        }

        private static string ResolveRequestUnavailableReason(
            Tenancy tenancy,
            bool isRequestingTenant,
            bool hasPendingRequest,
            bool hasPendingTerminationRequest,
            DateTimeOffset nowUtc)
        {
            if (!isRequestingTenant) return "Only a main tenant or co-tenant can request a renewal.";
            if (tenancy.TerminatedAt.HasValue) return "A terminated tenancy cannot be renewed.";
            if (!tenancy.EndDate.HasValue) return "A tenancy without an end date cannot be renewed.";
            if (tenancy.EndDate.Value.Date < nowUtc.Date) return "An expired tenancy cannot be renewed.";
            if (hasPendingRequest) return "A renewal request is already pending for this tenancy.";
            if (hasPendingTerminationRequest) return "A tenancy end request is already pending for this tenancy.";
            return string.Empty;
        }

        private static TenancyExtensionRequestDto MapRequest(TenancyExtensionRequest request)
        {
            return new TenancyExtensionRequestDto
            {
                Id = request.Id,
                TenancyId = request.TenancyId,
                RequestedById = request.RequestedById,
                RequestedByName = request.RequestedBy?.FullName ?? request.RequestedBy?.Email ?? "Tenant",
                OriginalEndDate = request.OriginalEndDate,
                ProposedEndDate = request.ProposedEndDate,
                Status = request.Status,
                ReviewedByName = request.ApprovedBy?.FullName ?? request.ApprovedBy?.Email,
                ReviewedAt = request.ApprovedAt,
                RejectionReason = request.RejectionReason,
                CreatedAt = request.CreatedAt
            };
        }

        private async Task TrySendSubmissionEmailAsync(
            TenancyExtensionRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                await _renewalEmailService.SendRequestSubmittedAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Renewal request {RequestId} was saved but its notification email failed.", request.Id);
            }
        }

        private async Task TrySendDecisionEmailAsync(
            TenancyExtensionRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                await _renewalEmailService.SendDecisionAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Renewal request {RequestId} was decided but its notification email failed.", request.Id);
            }
        }

        private async Task TrySendWithdrawalEmailAsync(
            TenancyExtensionRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                await _renewalEmailService.SendRequestWithdrawnAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Renewal request {RequestId} was cancelled but its notification email failed.", request.Id);
            }
        }
    }
}
