using Common.CommunicationModels;
using Common.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentHub.Portal.Services;

namespace RentHub.Portal.Controllers
{
    [Route("[controller]")]
    public class ReceiptsController : Controller
    {
        private readonly RentHubApiClient _api;
        private readonly ILogger<ReceiptsController> _logger;

        public ReceiptsController(RentHubApiClient api, ILogger<ReceiptsController> logger)
        {
            _api = api;
            _logger = logger;
        }

        [HttpGet("Verify/{code}")]
        [AllowAnonymous]
        public async Task<IActionResult> Verify(string code)
        {
            if (User.Identity?.IsAuthenticated == true && User.IsInRole("Admin"))
            {
                return Forbid();
            }

            try
            {
                var receipt = await _api.GetAnonymousAsync<RentReceiptVerificationDto>($"receipts/verify/{Uri.EscapeDataString(code ?? string.Empty)}");
                return View(receipt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Public receipt verification failed for code {Code}.", code);
                return View(new RentReceiptVerificationDto { IsValid = false });
            }
        }

        [HttpGet("Payment/{paymentId:int}")]
        [Authorize(Roles = "Landlord,Manager,Tenant")]
        public async Task<IActionResult> Payment(int paymentId)
        {
            try
            {
                var receipt = await _api.GetAsync<RentReceiptDto>($"receipts/payment/{paymentId}");
                return View(receipt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Receipt view failed for payment {PaymentId}.", paymentId);
                TempData["Error"] = "Unable to open the receipt right now.";
                return RedirectToAction("Index", "Home");
            }
        }

        [HttpGet("Payment/{paymentId:int}/Download")]
        [Authorize(Roles = "Landlord,Manager,Tenant")]
        public async Task<IActionResult> DownloadPayment(int paymentId)
        {
            try
            {
                var receipt = await _api.GetAsync<RentReceiptDto>($"receipts/payment/{paymentId}");
                var pdf = RentReceiptPdfBuilder.Build(receipt);
                var fileName = $"{SafeFileName(receipt.ReceiptNumber)}.pdf";
                return File(pdf, "application/pdf", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Receipt download failed for payment {PaymentId}.", paymentId);
                TempData["Error"] = "Unable to download the receipt right now.";
                return RedirectToAction("Index", "Home");
            }
        }

        private static string SafeFileName(string value)
        {
            var fallback = string.IsNullOrWhiteSpace(value) ? "rent-receipt" : value.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                fallback = fallback.Replace(c, '-');
            }

            return fallback;
        }
    }
}
