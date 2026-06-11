using System.Net;
using System.Text;
using Common.CommunicationModels;
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
        [Authorize]
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
        [Authorize]
        public async Task<IActionResult> DownloadPayment(int paymentId)
        {
            try
            {
                var receipt = await _api.GetAsync<RentReceiptDto>($"receipts/payment/{paymentId}");
                var html = BuildDownloadHtml(receipt);
                var bytes = Encoding.UTF8.GetBytes(html);
                var fileName = $"{SafeFileName(receipt.ReceiptNumber)}.html";
                return File(bytes, "text/html", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Receipt download failed for payment {PaymentId}.", paymentId);
                TempData["Error"] = "Unable to download the receipt right now.";
                return RedirectToAction("Index", "Home");
            }
        }

        private static string BuildDownloadHtml(RentReceiptDto receipt)
        {
            static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
            static string D(DateTimeOffset value) => value.LocalDateTime.ToString("dd MMM yyyy");
            static string M(decimal value) => value.ToString("N0");

            var method = receipt.Method.ToString();
            if (string.Equals(method, "Cash", StringComparison.OrdinalIgnoreCase))
            {
                method = "Cash / off-platform";
            }

            return $$$"""
<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <title>{{{H(receipt.ReceiptNumber)}}}</title>
    <style>
        body { margin: 0; background: #f3f6fb; color: #14213d; font-family: Arial, sans-serif; }
        .page { width: 820px; margin: 32px auto; background: #fff; border: 1px solid #d8e2ee; padding: 42px; }
        .top { display: flex; justify-content: space-between; gap: 24px; border-bottom: 4px solid #0f766e; padding-bottom: 24px; }
        h1 { margin: 0; font-size: 42px; letter-spacing: 4px; }
        .stamp { font-weight: 700; color: #0f766e; }
        .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 24px; margin-top: 28px; }
        .box { border: 1px solid #d8e2ee; padding: 18px; }
        table { width: 100%; border-collapse: collapse; margin-top: 28px; }
        th { background: #172033; color: #fff; text-align: left; padding: 12px; }
        td { padding: 14px 12px; border-bottom: 1px solid #e5edf5; }
        .total { text-align: right; font-size: 24px; font-weight: 800; margin-top: 24px; }
        .verify { display: flex; justify-content: space-between; gap: 24px; align-items: end; margin-top: 36px; border-top: 1px solid #d8e2ee; padding-top: 24px; }
        .qr svg { width: 150px; height: 150px; }
        small { color: #64748b; }
    </style>
</head>
<body>
    <main class="page">
        <section class="top">
            <div>
                <small>LONTSI HOMES</small>
                <h1>RECEIPT</h1>
            </div>
            <div>
                <div class="stamp">{{{H(receipt.ReceiptNumber)}}}</div>
                <small>Issued {{{D(receipt.IssuedAt)}}}</small>
            </div>
        </section>
        <section class="grid">
            <div class="box">
                <strong>Tenant</strong><br>
                {{{H(receipt.TenantName)}}}<br>
                <small>{{{H(receipt.TenantEmail)}}}</small>
            </div>
            <div class="box">
                <strong>Landlord</strong><br>
                {{{H(receipt.LandlordName)}}}<br>
                <small>{{{H(receipt.LandlordEmail)}}}</small>
            </div>
            <div class="box">
                <strong>Rental unit</strong><br>
                {{{H(receipt.PropertyName)}}}<br>
                <small>{{{H(receipt.ApartmentName)}}}</small>
            </div>
            <div class="box">
                <strong>Payment</strong><br>
                {{{H(method)}}}<br>
                <small>{{{H(receipt.TransactionId)}}}</small>
            </div>
        </section>
        <table>
            <thead><tr><th>Description</th><th>Period</th><th style="text-align:right">Amount</th></tr></thead>
            <tbody><tr><td>Rent payment</td><td>{{{H(receipt.PeriodLabel)}}}</td><td style="text-align:right">{{{M(receipt.Amount)}}} {{{H(receipt.Currency)}}}</td></tr></tbody>
        </table>
        <div class="total">TOTAL: {{{M(receipt.Amount)}}} {{{H(receipt.Currency)}}}</div>
        <section class="verify">
            <div>
                <strong>System verification</strong><br>
                <small>Scan the QR code or open this URL to verify the receipt.</small><br>
                <small>{{{H(receipt.VerificationUrl)}}}</small>
            </div>
            <div class="qr">{{{receipt.QrCodeSvg}}}</div>
        </section>
    </main>
</body>
</html>
""";
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
