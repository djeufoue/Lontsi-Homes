using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using Common.CommunicationModels;
using RentHub.API.Models.Entities;
using Common.Enums;
using RentHub.API.Services.Payments;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using RentHub.API.Helpers;
using Common.Helpers;
using RentHub.API.Services.Receipts;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PaymentsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IPaymentService _orangeMoneyService;
        private readonly MomoService _momoService;
        private readonly CardPaymentService _cardPaymentService;
        private readonly IRentReceiptService _receiptService;

        public PaymentsController(ApplicationDbContext context,
            IPaymentService orangeMoneyService,
            MomoService momoService,
            CardPaymentService cardPaymentService,
            IRentReceiptService receiptService)
        {
            _context = context;
            _orangeMoneyService = orangeMoneyService;
            _momoService = momoService;
            _cardPaymentService = cardPaymentService;
            _receiptService = receiptService;
        }

        /// <summary>
        /// Initiates a payment from a tenant to a landlord using the specified method.
        /// The caller must provide valid tenant and landlord identifiers as well as
        /// the amount and phone numbers (for mobile money).
        /// </summary>
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> CreatePayment([FromBody] PaymentRequest request)
        {
            try
            {
                // Validate tenant and landlord IDs
                if (string.IsNullOrEmpty(request.TenantId) || string.IsNullOrEmpty(request.LandlordId))
                {
                    return BadRequest("TenantId and LandlordId are required.");
                }
                // Ensure number of periods is at least 1
                if (request.NumberOfPeriods <= 0)
                {
                    return BadRequest("NumberOfPeriods must be at least 1.");
                }
                // Verify existence of tenant and landlord
                var tenant = await _context.Users.FindAsync(request.TenantId);
                var landlord = await _context.Users.FindAsync(request.LandlordId);
                if (tenant == null || landlord == null)
                {
                    return NotFound("Tenant or landlord not found.");
                }
                // Determine rent amount from the tenancy, if present
                // Find the active tenancy between this tenant and landlord
                var tenancy = await _context.Tenancies
                    .Include(t => t.Apartment!.Property)
                    .Include(t => t.Members)
                     .FirstOrDefaultAsync(t => t.Members.Any(m => !m.IsDeleted && m.MemberId == request.TenantId) && t.Apartment != null && t.Apartment!.Property!.LandlordId == request.LandlordId &&
                        (t.EndDate == null || t.EndDate >= DateTime.UtcNow) && t.StartDate <= DateTime.UtcNow);
                decimal finalAmount;
                if (tenancy != null)
                {
                    finalAmount = tenancy.MonthlyRent * request.NumberOfPeriods;
                }
                else
                {
                    // If no active tenancy is found, fall back to provided amount if positive
                    if (request.Amount <= 0)
                    {
                        return BadRequest("No active tenancy found and amount is invalid. Provide a positive amount or ensure tenancy exists.");
                    }
                    finalAmount = request.Amount;
                }
                // Extract current user ID (the caller)
                var currentUserId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(currentUserId))
                {
                    return Unauthorized();
                }

                if (!string.Equals(currentUserId, request.TenantId, StringComparison.Ordinal))
                {
                    return Forbid();
                }

                var requestKey = BuildPaymentRequestKey(request, currentUserId, tenancy?.Id, finalAmount);
                var existingPayment = await _context.Payments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(p => p.RequestKey == requestKey && !p.IsDeleted);

                if (existingPayment != null)
                {
                    return Ok(new
                    {
                        existingPayment.Id,
                        Status = existingPayment.Status.ToString(),
                        existingPayment.TransactionId,
                        Duplicate = true
                    });
                }

                // Create payment record with audit info
                var payment = new Payment
                {
                    TenantId = request.TenantId,
                    LandlordId = request.LandlordId,
                    Amount = finalAmount,
                    Currency = "XAF",
                    Method = request.Method,
                    RequestKey = requestKey,
                    Status = PaymentStatusEnum.Pending,
                    CreatedBy = currentUserId,
                    CreatedAt = DateTime.UtcNow,
                    IsDeleted = false
                };
                // Associate payment with the tenancy if available
                if (tenancy != null)
                {
                    payment.TenancyId = tenancy.Id;
                }
                _context.Payments.Add(payment);
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
                {
                    existingPayment = await _context.Payments
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(p => p.RequestKey == requestKey && !p.IsDeleted);

                    if (existingPayment != null)
                    {
                        return Ok(new
                        {
                            existingPayment.Id,
                            Status = existingPayment.Status.ToString(),
                            existingPayment.TransactionId,
                            Duplicate = true
                        });
                    }

                    throw;
                }

                // Set request amount for processing to finalAmount to ensure payment services use this value
                request.Amount = finalAmount;
                // Determine which service to use
                PaymentResult result;
                switch (request.Method)
                {
                    case PaymentMethodEnum.OrangeMoney:
                        result = await _orangeMoneyService.ProcessPaymentAsync(payment, request);
                        break;
                    case PaymentMethodEnum.Momo:
                        result = await _momoService.ProcessPaymentAsync(payment, request);
                        break;
                    case PaymentMethodEnum.Card:
                        result = await _cardPaymentService.ProcessPaymentAsync(payment, request);
                        break;
                    default:
                        result = new PaymentResult { Success = false, Status = "UNKNOWN" };
                        break;
                }
                // Update payment record with final status and audit info
                if (Enum.TryParse<PaymentStatusEnum>(result.Status, true, out var statusEnum))
                {
                    payment.Status = statusEnum;
                }

                if (!string.IsNullOrWhiteSpace(result.TransactionId))
                {
                    payment.TransactionId = result.TransactionId;
                }

                payment.ProviderReceiptUrl = result.ProviderReceiptUrl;

                // Mark update fields
                payment.UpdatedBy = currentUserId;
                payment.UpdatedAt = DateTime.UtcNow;
                _context.Payments.Update(payment);
                try
                {
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex) && !string.IsNullOrWhiteSpace(result.TransactionId))
                {
                    existingPayment = await _context.Payments
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(p => p.TransactionId == result.TransactionId && !p.IsDeleted);

                    if (existingPayment != null)
                    {
                        return Ok(new
                        {
                            existingPayment.Id,
                            Status = existingPayment.Status.ToString(),
                            existingPayment.TransactionId,
                            Duplicate = true
                        });
                    }

                    throw;
                }

                if (!result.Success)
                {
                    return BadRequest(new { payment.Id, result.Status, result.ProviderResponse, Duplicate = false });
                }
                return Ok(new { payment.Id, result.Status, payment.TransactionId, Duplicate = false });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPost("rent-periods")]
        [Authorize]
        public async Task<IActionResult> PayRentPeriods([FromBody] PayRentPeriodsRequest request)
        {
            try
            {
                if (request.TenancyId <= 0)
                {
                    return BadRequest("TenancyId is required.");
                }

                if (request.NumberOfPeriods <= 0)
                {
                    return BadRequest("NumberOfPeriods must be at least 1.");
                }

                var currentUserId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(currentUserId))
                {
                    return Unauthorized();
                }

                var tenancy = await _context.Tenancies
                    .Include(t => t.Apartment)
                    .ThenInclude(a => a.Property)
                    .Include(t => t.Members)
                    .Include(t => t.RentPeriods)
                    .FirstOrDefaultAsync(t => t.Id == request.TenancyId && !t.IsDeleted);

                if (tenancy == null)
                {
                    return NotFound("Tenancy not found.");
                }

                if (tenancy.Apartment?.Property == null)
                {
                    return NotFound("Property not found.");
                }

                var isTenantMember = tenancy.Members.Any(member => !member.IsDeleted && member.MemberId == currentUserId);
                if (!isTenantMember)
                {
                    return Forbid();
                }

                var payablePeriods = tenancy.RentPeriods
                    .Where(period =>
                        !period.IsDeleted &&
                        !RentPeriodScheduleHelper.IsPaidStatus(period.Status) &&
                        period.Status != RentPeriodStatusEnum.PendingPayment)
                    .OrderBy(period => period.PeriodStart)
                    .Take(request.NumberOfPeriods)
                    .ToList();

                if (payablePeriods.Count == 0)
                {
                    return BadRequest("There is no unpaid rent period to pay.");
                }

                if (payablePeriods.Count < request.NumberOfPeriods)
                {
                    return BadRequest($"Only {payablePeriods.Count} unpaid rent period(s) are available.");
                }

                var totalAmount = payablePeriods.Sum(period => period.Amount - period.PaidAmount);
                if (totalAmount <= 0)
                {
                    return BadRequest("The selected rent periods do not have a positive amount due.");
                }

                var paymentRequest = new PaymentRequest
                {
                    TenantId = currentUserId,
                    LandlordId = tenancy.Apartment.Property.LandlordId,
                    Amount = totalAmount,
                    Method = request.Method,
                    NumberOfPeriods = request.NumberOfPeriods,
                    IdempotencyKey = $"rent-periods:{request.TenancyId}:{string.Join(",", payablePeriods.Select(period => period.Id))}"
                };

                var requestKey = BuildPaymentRequestKey(paymentRequest, currentUserId, tenancy.Id, totalAmount);
                var existingPayment = await _context.Payments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(p => p.RequestKey == requestKey && !p.IsDeleted);

                if (existingPayment != null)
                {
                    return Ok(new
                    {
                        existingPayment.Id,
                        Status = existingPayment.Status.ToString(),
                        existingPayment.TransactionId,
                        Duplicate = true
                    });
                }

                var payment = new Payment
                {
                    TenantId = currentUserId,
                    LandlordId = tenancy.Apartment.Property.LandlordId,
                    TenancyId = tenancy.Id,
                    Amount = totalAmount,
                    Currency = "XAF",
                    Method = request.Method,
                    RequestKey = requestKey,
                    Status = PaymentStatusEnum.Pending,
                    CreatedBy = currentUserId,
                    CreatedAt = DateTime.UtcNow,
                    IsDeleted = false
                };

                _context.Payments.Add(payment);
                await _context.SaveChangesAsync();

                PaymentResult result = request.Method switch
                {
                    PaymentMethodEnum.OrangeMoney => await _orangeMoneyService.ProcessPaymentAsync(payment, paymentRequest),
                    PaymentMethodEnum.Momo => await _momoService.ProcessPaymentAsync(payment, paymentRequest),
                    PaymentMethodEnum.Card => await _cardPaymentService.ProcessPaymentAsync(payment, paymentRequest),
                    _ => new PaymentResult { Success = false, Status = "UNKNOWN" }
                };

                if (Enum.TryParse<PaymentStatusEnum>(result.Status, true, out var statusEnum))
                {
                    payment.Status = statusEnum;
                }

                if (!string.IsNullOrWhiteSpace(result.TransactionId))
                {
                    payment.TransactionId = result.TransactionId;
                }

                payment.ProviderReceiptUrl = result.ProviderReceiptUrl;
                payment.UpdatedBy = currentUserId;
                payment.UpdatedAt = DateTime.UtcNow;

                if (result.Success)
                {
                    foreach (var period in payablePeriods)
                    {
                        period.Status = RentPeriodStatusEnum.Paid;
                        period.PaidAmount = period.Amount;
                        period.PaidDate = DateTimeOffset.UtcNow;
                        period.PaymentId = payment.Id;
                        period.PaymentReference = payment.TransactionId;
                        period.UpdatedBy = currentUserId;
                        period.UpdatedAt = DateTimeOffset.UtcNow;
                    }
                }

                await _context.SaveChangesAsync();

                if (!result.Success)
                {
                    return BadRequest(new { payment.Id, result.Status, result.ProviderResponse, Duplicate = false });
                }

                var receipt = await _receiptService.EnsureReceiptAsync(payment.Id, currentUserId);
                if (receipt != null)
                {
                    await _receiptService.SendReceiptNotificationsAsync(
                        receipt,
                        notifyTenant: true,
                        notifyLandlord: true);
                }

                return Ok(new
                {
                    payment.Id,
                    result.Status,
                    payment.TransactionId,
                    ReceiptNumber = receipt?.ReceiptNumber ?? string.Empty,
                    CoveredPeriodIds = payablePeriods.Select(period => period.Id).ToList(),
                    Duplicate = false
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        [HttpPost("rent-periods/{rentPeriodId:int}/mark-paid")]
        [Authorize]
        public async Task<IActionResult> MarkRentPeriodAsPaid(int rentPeriodId, [FromBody] MarkRentPeriodPaidRequest? request)
        {
            try
            {
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrWhiteSpace(userId))
                {
                    return Unauthorized();
                }

                var period = await _context.RentPeriods
                    .Include(rp => rp.Tenancy)
                    .ThenInclude(t => t!.Apartment)
                    .ThenInclude(a => a!.Property)
                    .Include(rp => rp.Tenancy)
                    .ThenInclude(t => t!.Members)
                    .ThenInclude(m => m.Member)
                    .FirstOrDefaultAsync(rp => rp.Id == rentPeriodId && !rp.IsDeleted);

                if (period == null)
                {
                    return NotFound("Rent period not found.");
                }

                var tenancy = period.Tenancy;
                if (tenancy?.Apartment?.Property == null)
                {
                    return NotFound("Tenancy or property not found.");
                }

                if (!await CanWriteTenancyAsync(tenancy, userId))
                {
                    return Forbid();
                }

                if (RentPeriodScheduleHelper.IsPaidStatus(period.Status))
                {
                    return BadRequest("This rent period is already paid or closed.");
                }

                if (period.Status == RentPeriodStatusEnum.PendingPayment)
                {
                    return BadRequest("This rent period already has a pending payment.");
                }

                var tenancyPeriods = await _context.RentPeriods
                    .Where(rp => rp.TenancyId == tenancy.Id && !rp.IsDeleted)
                    .OrderBy(rp => rp.PeriodStart)
                    .ToListAsync();

                var firstUnpaid = tenancyPeriods.FirstOrDefault(rp =>
                    !RentPeriodScheduleHelper.IsPaidStatus(rp.Status) &&
                    rp.Status != RentPeriodStatusEnum.PendingPayment);

                if (firstUnpaid == null || firstUnpaid.Id != period.Id)
                {
                    return BadRequest("Previous unpaid rent periods must be marked paid first.");
                }

                var mainTenant = tenancy.Members
                    .Where(member => !member.IsDeleted)
                    .OrderBy(member => member.Role == TenancyMemberRoleEnum.MainTenant ? 0 : 1)
                    .ThenBy(member => member.CreatedAt)
                    .FirstOrDefault();

                if (mainTenant == null)
                {
                    return BadRequest("A tenant member is required before recording a cash rent payment.");
                }

                var amountDue = period.Amount - period.PaidAmount;
                if (amountDue <= 0)
                {
                    amountDue = period.Amount;
                }

                var requestKey = $"manual-rent-period:{period.Id}";
                var existingPayment = await _context.Payments
                    .FirstOrDefaultAsync(payment => payment.RequestKey == requestKey && !payment.IsDeleted);

                if (existingPayment != null)
                {
                    var existingReceipt = await _receiptService.EnsureReceiptAsync(existingPayment.Id, userId);
                    return Ok(new
                    {
                        existingPayment.Id,
                        Status = existingPayment.Status.ToString(),
                        existingPayment.TransactionId,
                        ReceiptNumber = existingReceipt?.ReceiptNumber ?? string.Empty,
                        Duplicate = true
                    });
                }

                var paidDate = request?.PaidDate ?? DateTimeOffset.UtcNow;
                var payment = new Payment
                {
                    TenantId = mainTenant.MemberId,
                    LandlordId = tenancy.Apartment.Property.LandlordId,
                    TenancyId = tenancy.Id,
                    Amount = amountDue,
                    Currency = "XAF",
                    Method = PaymentMethodEnum.Cash,
                    RequestKey = requestKey,
                    TransactionId = $"manual-{Guid.NewGuid():N}",
                    Status = PaymentStatusEnum.Success,
                    PaymentDate = paidDate,
                    CreatedBy = userId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IsDeleted = false
                };

                _context.Payments.Add(payment);
                await _context.SaveChangesAsync();

                period.Status = RentPeriodStatusEnum.Paid;
                period.PaidAmount = period.Amount;
                period.PaidDate = paidDate;
                period.PaymentId = payment.Id;
                period.PaymentReference = payment.TransactionId;
                period.UpdatedBy = userId;
                period.UpdatedAt = DateTimeOffset.UtcNow;

                await _context.SaveChangesAsync();

                var receipt = await _receiptService.EnsureReceiptAsync(payment.Id, userId);
                if (receipt != null)
                {
                    await _receiptService.SendReceiptNotificationsAsync(
                        receipt,
                        notifyTenant: true,
                        notifyLandlord: !string.Equals(payment.LandlordId, userId, StringComparison.Ordinal));
                }

                return Ok(new
                {
                    payment.Id,
                    Status = payment.Status.ToString(),
                    payment.TransactionId,
                    ReceiptNumber = receipt?.ReceiptNumber ?? string.Empty,
                    Duplicate = false
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        /// <summary>
        /// Marks a payment as paid (success) manually.  Only the landlord of the payment or an
        /// authorized manager/owner with write permission may perform this action.  Useful if
        /// the tenant cannot complete the payment through the system.
        /// </summary>
        [HttpPut("{id}/mark-paid")]
        [Authorize]
        public async Task<IActionResult> MarkPaymentAsPaid(int id)
        {
            try
            {
                var payment = await _context.Payments
                    .Include(p => p.Tenancy)
                    .ThenInclude(t => t.Apartment)
                    .FirstOrDefaultAsync(p => p.Id == id);
                if (payment == null) return NotFound("Payment not found.");
                var userId = UserHelpers.GetUserId(User);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                // Determine if user can mark payment as paid
                bool isLandlord = payment.LandlordId == userId;
                bool canWrite = false;
                if (isLandlord)
                {
                    canWrite = true;
                }
                else if (payment.Tenancy != null)
                {
                    var tenancy = payment.Tenancy;
                    // Owner with write permission on the apartment
                    var ownerWrite = await _context.ApartmentOwners
                        .AnyAsync(o => o.ApartmentId == tenancy.ApartmentId && o.OwnerId == userId && o.Permission == PermissionLevelEnum.ReadWrite);
                    // Manager with write permission on the property
                    var managerWrite = await _context.PropertyManagerAssignments
                        .AnyAsync(m => m.PropertyId == tenancy.Apartment!.PropertyId && m.ManagerId == userId && m.Permission == PermissionLevelEnum.ReadWrite);
                    canWrite = ownerWrite || managerWrite;
                }
                if (!canWrite)
                {
                    return Forbid();
                }
                if (payment.Status == PaymentStatusEnum.Success)
                {
                    return BadRequest("Payment is already marked as paid.");
                }
                payment.Status = PaymentStatusEnum.Success;
                payment.UpdatedBy = userId;
                payment.UpdatedAt = DateTime.UtcNow;
                _context.Payments.Update(payment);
                await _context.SaveChangesAsync();
                var receipt = await _receiptService.EnsureReceiptAsync(payment.Id, userId);
                if (receipt != null)
                {
                    await _receiptService.SendReceiptNotificationsAsync(
                        receipt,
                        notifyTenant: true,
                        notifyLandlord: !string.Equals(payment.LandlordId, userId, StringComparison.Ordinal));
                }

                return Ok(new { Message = "Payment marked as paid.", PaymentId = payment.Id, ReceiptNumber = receipt?.ReceiptNumber ?? string.Empty });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }

        private static string BuildPaymentRequestKey(
            PaymentRequest request,
            string currentUserId,
            int? tenancyId,
            decimal finalAmount)
        {
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                return HashKey($"client|{currentUserId}|{request.IdempotencyKey.Trim()}");
            }

            var fiveMinuteBucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300;
            var rawKey = string.Join(
                "|",
                "server",
                currentUserId,
                request.TenantId,
                request.LandlordId,
                tenancyId?.ToString(CultureInfo.InvariantCulture) ?? "none",
                request.Method.ToString(),
                finalAmount.ToString("0.00", CultureInfo.InvariantCulture),
                request.NumberOfPeriods.ToString(CultureInfo.InvariantCulture),
                fiveMinuteBucket.ToString(CultureInfo.InvariantCulture));

            return HashKey(rawKey);
        }

        private static string HashKey(string value)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static bool IsUniqueConstraintViolation(DbUpdateException ex)
        {
            if (ex.GetBaseException() is SqlException sqlException)
            {
                return sqlException.Number is 2601 or 2627;
            }

            var message = ex.GetBaseException().Message;
            return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<bool> CanWriteTenancyAsync(Tenancy tenancy, string userId)
        {
            if (tenancy.Apartment?.Property == null)
            {
                return false;
            }

            if (tenancy.Apartment.Property.LandlordId == userId)
            {
                return true;
            }

            var ownerWrite = await _context.ApartmentOwners.AnyAsync(o =>
                !o.IsDeleted &&
                o.ApartmentId == tenancy.ApartmentId &&
                o.OwnerId == userId &&
                o.Permission == PermissionLevelEnum.ReadWrite);

            if (ownerWrite)
            {
                return true;
            }

            return await _context.PropertyManagerAssignments.AnyAsync(m =>
                !m.IsDeleted &&
                m.PropertyId == tenancy.Apartment.PropertyId &&
                m.ManagerId == userId &&
                m.Permission == PermissionLevelEnum.ReadWrite);
        }
    }
}




