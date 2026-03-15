using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using Common.CommunicationModels;
using RentHub.API.Models.Entities;
using Common.Enums;
using RentHub.API.Services.Payments;
using System.Security.Claims;

using RentHub.API.Helpers;

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

        public PaymentsController(ApplicationDbContext context,
            IPaymentService orangeMoneyService,
            MomoService momoService,
            CardPaymentService cardPaymentService)
        {
            _context = context;
            _orangeMoneyService = orangeMoneyService;
            _momoService = momoService;
            _cardPaymentService = cardPaymentService;
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
                // Create payment record with audit info
                var payment = new Payment
                {
                    TenantId = request.TenantId,
                    LandlordId = request.LandlordId,
                    Amount = finalAmount,
                    Currency = "XAF",
                    Method = request.Method,
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
                await _context.SaveChangesAsync();
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
                // Mark update fields
                payment.UpdatedBy = currentUserId;
                payment.UpdatedAt = DateTime.UtcNow;
                _context.Payments.Update(payment);
                await _context.SaveChangesAsync();
                if (!result.Success)
                {
                    return BadRequest(new { payment.Id, result.Status, result.ProviderResponse });
                }
                return Ok(new { payment.Id, result.Status, result.TransactionId });
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
                return Ok(new { Message = "Payment marked as paid.", PaymentId = payment.Id });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Message = ex.Message });
            }
        }
    }
}




