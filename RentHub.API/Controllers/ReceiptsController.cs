using Common.CommunicationModels;
using Common.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Helpers;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Receipts;

namespace RentHub.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ReceiptsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IRentReceiptService _receiptService;

        public ReceiptsController(ApplicationDbContext context, IRentReceiptService receiptService)
        {
            _context = context;
            _receiptService = receiptService;
        }

        [HttpGet("payment/{paymentId:int}")]
        [Authorize]
        public async Task<IActionResult> GetByPayment(int paymentId)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized();
            }

            var payment = await LoadPaymentForAccessAsync(paymentId);
            if (payment == null)
            {
                return NotFound("Payment not found.");
            }

            if (!await CanAccessPaymentAsync(payment, userId))
            {
                return Forbid();
            }

            if (payment.Status != PaymentStatusEnum.Success)
            {
                return BadRequest("A receipt is available only after the payment succeeds.");
            }

            var receipt = await _receiptService.EnsureReceiptAsync(payment.Id, userId);
            return receipt == null ? NotFound("Receipt not found.") : Ok(receipt);
        }

        [HttpGet("verify/{verificationCode}")]
        [AllowAnonymous]
        public async Task<ActionResult<RentReceiptVerificationDto>> Verify(string verificationCode)
        {
            var receipt = await _receiptService.VerifyReceiptAsync(verificationCode);
            if (receipt == null)
            {
                return Ok(new RentReceiptVerificationDto { IsValid = false });
            }

            return Ok(receipt);
        }

        private Task<Payment?> LoadPaymentForAccessAsync(int paymentId)
        {
            return _context.Payments
                .Include(p => p.Tenancy)
                .ThenInclude(t => t!.Apartment)
                .ThenInclude(a => a!.Property)
                .FirstOrDefaultAsync(p => p.Id == paymentId && !p.IsDeleted);
        }

        private async Task<bool> CanAccessPaymentAsync(Payment payment, string userId)
        {
            if (payment.TenantId == userId || payment.LandlordId == userId)
            {
                return true;
            }

            var tenancy = payment.Tenancy;
            if (tenancy?.Apartment?.Property == null)
            {
                return false;
            }

            var isOwner = await _context.ApartmentOwners.AnyAsync(owner =>
                !owner.IsDeleted &&
                owner.ApartmentId == tenancy.ApartmentId &&
                owner.OwnerId == userId);

            if (isOwner)
            {
                return true;
            }

            return await _context.PropertyManagerAssignments.AnyAsync(manager =>
                !manager.IsDeleted &&
                manager.PropertyId == tenancy.Apartment.PropertyId &&
                manager.ManagerId == userId);
        }
    }
}
