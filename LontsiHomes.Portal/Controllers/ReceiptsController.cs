using Common.CommunicationModels;
using Common.Enums;
using Common.Helpers;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LontsiHomes.Portal.Services;

namespace LontsiHomes.Portal.Controllers
{
    [Route("[controller]")]
    public class ReceiptsController : Controller
    {
        private readonly LontsiHomesApiClient _api;
        private readonly ILogger<ReceiptsController> _logger;

        public ReceiptsController(LontsiHomesApiClient api, ILogger<ReceiptsController> logger)
        {
            _api = api;
            _logger = logger;
        }

        [HttpGet("Verify/{code}")]
        [AllowAnonymous]
        public async Task<IActionResult> Verify(string code)
        {
            if (User.Identity?.IsAuthenticated != true)
            {
                return ReceiptAccessDenied();
            }

            try
            {
                var receipt = await _api.GetAsync<RentReceiptVerificationDto>($"receipts/verify/{Uri.EscapeDataString(code ?? string.Empty)}");
                ViewData["ReceiptAccessAuthorized"] = true;
                return View(receipt);
            }
            catch (Exception ex) when (ex.Message.Contains("RECEIPT_ACCESS_DENIED", StringComparison.OrdinalIgnoreCase) ||
                                       ex.Message.Contains("AUTH_SESSION_EXPIRED", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Receipt verification access was denied for the current user.");
                return ReceiptAccessDenied();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Receipt verification failed for the current user.");
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
                var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("fr", StringComparison.OrdinalIgnoreCase)
                    ? PlatformLanguage.French
                    : PlatformLanguage.English;
                var pdf = RentReceiptPdfBuilder.Build(receipt, language);
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
            var fallback = string.IsNullOrWhiteSpace(value) ? "rent-invoice" : value.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                fallback = fallback.Replace(c, '-');
            }

            return fallback;
        }

        private IActionResult ReceiptAccessDenied()
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            ViewData["ReceiptAccessAuthorized"] = false;
            return View("Verify", new RentReceiptVerificationDto { IsValid = false });
        }
    }
}
