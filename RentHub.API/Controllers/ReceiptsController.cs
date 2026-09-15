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
        private readonly WhatsAppReceiptMedia _media;

        public ReceiptsController(ApplicationDbContext context, IRentReceiptService receiptService, WhatsAppReceiptMedia media)
        {
            _context = context;
            _receiptService = receiptService;
            _media = media;
        }

        [HttpGet("whatsapp.pdf")]
        [AllowAnonymous]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> WhatsAppPdf([FromQuery] string? token, CancellationToken cancellationToken)
        {
            Response.Headers["Cache-Control"] = "no-store, private";
            Response.Headers["Referrer-Policy"] = "no-referrer";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            var grant = _media.Validate(token);
            if (grant == null) return NotFound();

            // A valid token cannot be used for a different tenant, revoked receipt or failed payment.
            // GetReceiptAsync excludes deleted payments. It does not create/modify a receipt.
            var receipt = await _receiptService.GetReceiptAsync(grant.PaymentId, cancellationToken);
            if (receipt == null || receipt.PaymentId != grant.PaymentId || receipt.Status != PaymentStatusEnum.Success ||
                receipt.TenantUserId != grant.RecipientUserId || receipt.VerificationCode != grant.VerificationCode)
                return NotFound();

            var language = grant.Language == "fr" ? PlatformLanguage.French : PlatformLanguage.English;
            return File(Common.Helpers.RentReceiptPdfBuilder.Build(receipt, language),
                "application/pdf", $"receipt-{receipt.PaymentId}.pdf");
        }

        [HttpGet("payment/{paymentId:int}")]
        [Authorize]
        public async Task<IActionResult> GetByPayment(int paymentId)
        {
            if (User.IsInRole("Admin"))
            {
                return Forbid();
            }

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

            if (payment.Status != PaymentStatusEnum.Success && payment.CorrectionJson == null)
            {
                return BadRequest("A receipt is available only after the payment succeeds.");
            }

            var receipt = payment.CorrectionJson != null
                ? await _receiptService.GetReceiptAsync(payment.Id)
                : await _receiptService.EnsureReceiptAsync(payment.Id, userId);
            return receipt == null ? NotFound("Receipt not found.") : Ok(receipt);
        }

        [HttpGet("verify/{verificationCode}")]
        [Authorize]
        public async Task<ActionResult<RentReceiptVerificationDto>> Verify(string verificationCode)
        {
            var userId = UserHelpers.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized();
            }

            var normalizedCode = verificationCode?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedCode))
            {
                return Ok(new RentReceiptVerificationDto { IsValid = false });
            }

            var payment = await _context.Payments
                .AsNoTracking()
                .Include(item => item.Tenancy)
                .ThenInclude(tenancy => tenancy!.Apartment)
                .ThenInclude(apartment => apartment!.Property)
                .FirstOrDefaultAsync(item =>
                    !item.IsDeleted &&
                    item.ReceiptVerificationCode == normalizedCode &&
                    (item.Status == PaymentStatusEnum.Success || item.CorrectionJson != null));

            if (payment == null)
            {
                return Ok(new RentReceiptVerificationDto { IsValid = false });
            }

            var property = payment.Tenancy?.Apartment?.Property;
            var isTenant = string.Equals(payment.TenantId, userId, StringComparison.Ordinal);
            var isLandlord = property != null &&
                string.Equals(property.LandlordId, userId, StringComparison.Ordinal);
            var isPropertyManager = property != null &&
                await _context.PropertyManagerAssignments.AnyAsync(assignment =>
                    !assignment.IsDeleted &&
                    assignment.PropertyId == property.Id &&
                    assignment.ManagerId == userId);

            if (!isTenant && !isLandlord && !isPropertyManager)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    Code = "RECEIPT_ACCESS_DENIED",
                    Message = "You are not authorized to view this invoice."
                });
            }

            var receipt = await _receiptService.VerifyReceiptAsync(normalizedCode);
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
