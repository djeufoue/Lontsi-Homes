using System.Data;
using System.Text.Json;
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
using RentHub.API.Services.Receipts;

namespace RentHub.API.Controllers;

[ApiController, Authorize]
[Route("api/payments/rent-periods/correct-manual")]
public sealed class ManualPaymentCorrectionsController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IManagerPermissionService _permissions;
    private readonly IRentReceiptService _receipts;

    public ManualPaymentCorrectionsController(ApplicationDbContext context, IManagerPermissionService permissions, IRentReceiptService receipts)
    { _context = context; _permissions = permissions; _receipts = receipts; }

    [HttpPost]
    public async Task<IActionResult> Correct(CorrectManualRentPaymentRequest request, CancellationToken cancellationToken)
    {
        var actor = UserHelpers.GetUserId(User);
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 5 ||
            request.Reason.Length > 1000 || request.RentPeriodIds == null || request.ExpectedPaymentIds == null ||
            request.RentPeriodIds.Count is < 1 or > 120 || request.RentPeriodIds.Distinct().Count() != request.RentPeriodIds.Count)
            return BadRequest("Select periods and provide a correction reason (5 to 1000 characters). Maximum: 120 periods.");
        if (!await _permissions.HasTenancyPermissionAsync(actor, request.TenancyId, ManagerPermission.DeletePayment, User.IsInRole("Admin")) ||
            !await _permissions.HasTenancyPermissionAsync(actor, request.TenancyId, ManagerPermission.MarkRentAsPaid, User.IsInRole("Admin")))
            return Forbid();

        // The range locks serialize changes with manual recording/other corrections.
        await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var requestId = request.RequestId.ToString();
        var previous = await _context.Payments.Where(p => p.TenancyId == request.TenancyId && p.CorrectionRequestId == requestId)
            .ToListAsync(cancellationToken);
        if (previous.Count > 0)
        {
            var audits = previous.Select(p => JsonSerializer.Deserialize<ManualPaymentCorrection>(p.CorrectionJson!)!).ToList();
            if (audits.Any(a => a.ActorId != actor || a.Reason != request.Reason.Trim()) ||
                !audits.SelectMany(a => a.ReleasedPeriodIds).OrderBy(id => id).SequenceEqual(request.RentPeriodIds.OrderBy(id => id)))
                return Conflict("This correction request has already been used. Reload the page.");
            return Ok(new { Corrected = true, Duplicate = true });
        }

        var periods = await _context.RentPeriods.Include(p => p.Payment)
            .Where(p => p.TenancyId == request.TenancyId && !p.IsDeleted)
            .OrderByDescending(p => p.PeriodStart).ThenByDescending(p => p.Id).ToListAsync(cancellationToken);
        // Do not filter out online, historical or pending payments: these are barriers,
        // not permission to skip newer periods and undo an older manual payment.
        var expected = periods.Where(p => RentPeriodScheduleHelper.IsPaidStatus(p.Status) ||
            p.Status == RentPeriodStatusEnum.PendingPayment || p.PaidAmount > 0).Take(request.RentPeriodIds.Count).ToList();
        if (expected.Count != request.RentPeriodIds.Count ||
            !expected.Select(p => p.Id).OrderBy(id => id).SequenceEqual(request.RentPeriodIds.OrderBy(id => id)))
            return Conflict("Corrections must start with the latest paid period and continue backwards without skipping a period. Reload the page.");
        if (expected.Any(p => p.Status != RentPeriodStatusEnum.Paid || p.Payment == null || p.Payment.IsDeleted ||
            p.Payment.Status != PaymentStatusEnum.Success || p.Payment.Method != PaymentMethodEnum.Cash ||
            !p.Payment.TransactionId.StartsWith("manual-", StringComparison.Ordinal) || p.Payment.CorrectionJson != null ||
            p.Payment.TenancyId != request.TenancyId || p.PaidAmount != p.Amount || p.PaidAmount <= 0 ||
            !request.ExpectedPaymentIds.TryGetValue(p.Id, out var paymentId) || paymentId != p.PaymentId))
            return Conflict("Only confirmed manual rent payments can be corrected. A newer payment may block this selection. Reload the page.");

        var selectedIds = expected.Select(p => p.Id).ToHashSet();
        var now = DateTimeOffset.UtcNow;
        var affected = expected.Select(p => p.Payment!).DistinctBy(p => p.Id).ToList();
        foreach (var original in affected)
        {
            var linked = periods.Where(p => p.PaymentId == original.Id).ToList();
            if (linked.Any(p => p.Status != RentPeriodStatusEnum.Paid || p.PaidAmount != p.Amount || p.PaidAmount <= 0) ||
                linked.Sum(p => p.PaidAmount) != original.Amount ||
                await _context.RentPeriods.IgnoreQueryFilters().AnyAsync(p => p.PaymentId == original.Id && (p.TenancyId != request.TenancyId || p.IsDeleted), cancellationToken))
                return Conflict("This payment needs an administrator review before it can be corrected.");

            var snapshot = await _receipts.EnsureReceiptAsync(original.Id, actor, cancellationToken)
                ?? throw new InvalidOperationException("Original receipt not found.");
            var retained = linked.Where(p => !selectedIds.Contains(p.Id)).ToList();
            Payment? replacement = null;
            if (retained.Count > 0)
            {
                replacement = new Payment {
                    TenantId = original.TenantId, LandlordId = original.LandlordId, TenancyId = original.TenancyId,
                    Amount = retained.Sum(p => p.PaidAmount), Currency = original.Currency, Method = PaymentMethodEnum.Cash,
                    Status = PaymentStatusEnum.Success, PaymentDate = original.PaymentDate,
                    RequestKey = $"manual-correction:{original.Id}:{requestId}", TransactionId = $"manual-{Guid.NewGuid():N}",
                    CreatedBy = actor, CreatedAt = now
                };
                _context.Payments.Add(replacement);
                await _context.SaveChangesAsync(cancellationToken);
            }
            original.Status = PaymentStatusEnum.Cancelled;
            original.RequestKey = $"corrected:{original.Id}:{requestId}";
            original.CorrectionRequestId = requestId;
            original.ReplacementPaymentId = replacement?.Id;
            original.UpdatedAt = now;
            original.UpdatedBy = actor;
            foreach (var period in linked)
            {
                if (selectedIds.Contains(period.Id))
                {
                    period.PaidAmount = 0;
                    period.PaidDate = null;
                    period.PaymentId = null;
                    period.PaymentReference = string.Empty;
                    period.Status = RentPeriodScheduleHelper.ResolveUnpaidStatus(period.DueDate, now);
                }
                else
                {
                    period.PaymentId = replacement!.Id;
                    period.PaymentReference = replacement.TransactionId;
                }
                period.UpdatedBy = actor;
                period.UpdatedAt = now;
            }
            await _context.SaveChangesAsync(cancellationToken);
            var replacementReceipt = replacement == null ? null : await _receipts.EnsureReceiptAsync(replacement.Id, actor, cancellationToken);
            original.CorrectionJson = JsonSerializer.Serialize(new ManualPaymentCorrection {
                ActorId = actor, CorrectedAt = now, Reason = request.Reason.Trim(),
                ReleasedPeriodIds = linked.Where(p => selectedIds.Contains(p.Id)).Select(p => p.Id).ToList(),
                OriginalReceipt = snapshot, ReplacementReceipt = replacementReceipt
            });
            var originalId = original.Id.ToString();
            var obsoleteMessages = await _context.NotificationDeliveries.Where(d => d.RelatedEntityId == originalId &&
                (d.EventType == "tenant_rent_payment_received" || d.EventType == "landlord_rent_payment_received") &&
                (d.Status == NotificationDeliveryStatuses.Pending || d.Status == NotificationDeliveryStatuses.Failed)).ToListAsync(cancellationToken);
            foreach (var delivery in obsoleteMessages)
            {
                delivery.Status = NotificationDeliveryStatuses.Skipped;
                delivery.LastErrorCode = "payment_corrected";
                delivery.ErrorMessage = "The original payment was corrected; do not send its receipt.";
            }
        }
        // CorrectionJson is also a durable notification queue, committed with the ledger.
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(new { Corrected = true, Duplicate = false, ReleasedPeriodIds = selectedIds });
    }
}
