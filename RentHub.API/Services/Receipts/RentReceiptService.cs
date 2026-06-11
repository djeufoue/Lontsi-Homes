using System.Security.Cryptography;
using System.IO;
using System.Net;
using System.Text;
using Common.CommunicationModels;
using Common.Enums;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Models.Entities;
using RentHub.API.Services.Email;

namespace RentHub.API.Services.Receipts
{
    public class RentReceiptService : IRentReceiptService
    {
        private const string TokenAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IEmailService _emailService;
        private readonly ILogger<RentReceiptService> _logger;

        public RentReceiptService(
            ApplicationDbContext context,
            IConfiguration configuration,
            IEmailService emailService,
            ILogger<RentReceiptService> logger)
        {
            _context = context;
            _configuration = configuration;
            _emailService = emailService;
            _logger = logger;
        }

        public async Task<RentReceiptDto?> EnsureReceiptAsync(int paymentId, string actorId, CancellationToken cancellationToken = default)
        {
            var payment = await LoadPaymentAsync(paymentId, cancellationToken);
            if (payment == null)
            {
                return null;
            }

            if (payment.Status != PaymentStatusEnum.Success)
            {
                throw new InvalidOperationException("Receipts can only be generated for successful payments.");
            }

            if (string.IsNullOrWhiteSpace(payment.SystemReceiptNumber) ||
                string.IsNullOrWhiteSpace(payment.ReceiptVerificationCode) ||
                payment.ReceiptIssuedAt == null)
            {
                payment.SystemReceiptNumber = await GenerateReceiptNumberAsync(cancellationToken);
                payment.ReceiptVerificationCode = await GenerateVerificationCodeAsync(cancellationToken);
                payment.ReceiptIssuedAt = DateTimeOffset.UtcNow;
                payment.UpdatedBy = actorId;
                payment.UpdatedAt = DateTimeOffset.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);
            }

            return await BuildDtoAsync(payment, cancellationToken);
        }

        public async Task<RentReceiptDto?> GetReceiptAsync(int paymentId, CancellationToken cancellationToken = default)
        {
            var payment = await LoadPaymentAsync(paymentId, cancellationToken);
            return payment == null ? null : await BuildDtoAsync(payment, cancellationToken);
        }

        public async Task<RentReceiptVerificationDto?> VerifyReceiptAsync(string verificationCode, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(verificationCode))
            {
                return null;
            }

            var normalized = verificationCode.Trim();
            var payment = await _context.Payments
                .AsNoTracking()
                .Include(p => p.Tenant)
                .Include(p => p.Landlord)
                .Include(p => p.Tenancy)
                .ThenInclude(t => t!.Apartment)
                .ThenInclude(a => a!.Property)
                .FirstOrDefaultAsync(p =>
                    !p.IsDeleted &&
                    p.ReceiptVerificationCode == normalized &&
                    p.Status == PaymentStatusEnum.Success,
                    cancellationToken);

            if (payment == null)
            {
                return null;
            }

            var periods = await _context.RentPeriods
                .AsNoTracking()
                .Where(period => period.PaymentId == payment.Id && !period.IsDeleted)
                .OrderBy(period => period.PeriodStart)
                .ToListAsync(cancellationToken);

            return new RentReceiptVerificationDto
            {
                IsValid = true,
                ReceiptNumber = payment.SystemReceiptNumber ?? string.Empty,
                IssuedAt = payment.ReceiptIssuedAt,
                PaymentDate = payment.PaymentDate,
                Amount = payment.Amount,
                Currency = payment.Currency,
                PaymentMethod = payment.Method == PaymentMethodEnum.Cash ? "Cash / off-platform" : payment.Method.ToString(),
                TenantName = payment.Tenant?.FullName ?? payment.Tenant?.Email ?? "Tenant",
                LandlordName = payment.Landlord?.FullName ?? payment.Landlord?.Email ?? "Landlord",
                PropertyName = payment.Tenancy?.Apartment?.Property?.Name ?? string.Empty,
                ApartmentName = payment.Tenancy?.Apartment?.Name ?? string.Empty,
                PeriodLabel = BuildPeriodLabel(periods)
            };
        }

        public async Task SendReceiptNotificationsAsync(
            RentReceiptDto receipt,
            bool notifyTenant,
            bool notifyLandlord,
            CancellationToken cancellationToken = default)
        {
            var attachment = BuildReceiptAttachment(receipt);
            if (notifyTenant && !string.IsNullOrWhiteSpace(receipt.TenantEmail))
            {
                await TrySendEmailAsync(
                    receipt.TenantEmail,
                    $"Rent receipt {receipt.ReceiptNumber}",
                    BuildTenantReceiptEmail(receipt),
                    attachment,
                    cancellationToken);
            }

            if (notifyLandlord && !string.IsNullOrWhiteSpace(receipt.LandlordEmail))
            {
                await TrySendEmailAsync(
                    receipt.LandlordEmail,
                    $"Rent payment received - {receipt.ReceiptNumber}",
                    BuildLandlordPaymentEmail(receipt),
                    attachment,
                    cancellationToken);
            }
        }

        private async Task<Payment?> LoadPaymentAsync(int paymentId, CancellationToken cancellationToken)
        {
            return await _context.Payments
                .Include(p => p.Tenant)
                .Include(p => p.Landlord)
                .Include(p => p.Tenancy)
                .ThenInclude(t => t!.Apartment)
                .ThenInclude(a => a!.Property)
                .FirstOrDefaultAsync(p => p.Id == paymentId && !p.IsDeleted, cancellationToken);
        }

        private async Task<RentReceiptDto> BuildDtoAsync(Payment payment, CancellationToken cancellationToken)
        {
            var periods = await _context.RentPeriods
                .AsNoTracking()
                .Where(period => period.PaymentId == payment.Id && !period.IsDeleted)
                .OrderBy(period => period.PeriodStart)
                .ToListAsync(cancellationToken);

            var verificationCode = payment.ReceiptVerificationCode ?? string.Empty;
            var verificationUrl = BuildVerificationUrl(verificationCode);
            var qrCodeSvg = string.IsNullOrWhiteSpace(verificationUrl)
                ? string.Empty
                : SimpleQrCodeGenerator.CreateSvg(verificationUrl);

            return new RentReceiptDto
            {
                PaymentId = payment.Id,
                TenancyId = payment.TenancyId,
                ReceiptNumber = payment.SystemReceiptNumber ?? string.Empty,
                VerificationCode = verificationCode,
                VerificationUrl = verificationUrl,
                QrCodeSvg = qrCodeSvg,
                IssuedAt = payment.ReceiptIssuedAt ?? payment.PaymentDate,
                PaymentDate = payment.PaymentDate,
                Amount = payment.Amount,
                Currency = payment.Currency,
                Method = payment.Method,
                Status = payment.Status,
                TransactionId = payment.TransactionId,
                ProviderReceiptUrl = payment.ProviderReceiptUrl ?? string.Empty,
                TenantName = payment.Tenant?.FullName ?? payment.Tenant?.Email ?? "Tenant",
                TenantEmail = payment.Tenant?.Email ?? string.Empty,
                LandlordName = payment.Landlord?.FullName ?? payment.Landlord?.Email ?? "Landlord",
                LandlordEmail = payment.Landlord?.Email ?? string.Empty,
                PropertyName = payment.Tenancy?.Apartment?.Property?.Name ?? string.Empty,
                ApartmentName = payment.Tenancy?.Apartment?.Name ?? string.Empty,
                PeriodLabel = BuildPeriodLabel(periods),
                PeriodStart = periods.FirstOrDefault()?.PeriodStart,
                PeriodEnd = periods.LastOrDefault()?.PeriodEnd,
                IsValid = true
            };
        }

        private async Task<string> GenerateReceiptNumberAsync(CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var candidate = $"RH-RCPT-{DateTimeOffset.UtcNow:yyyyMMdd}-{RandomNumberGenerator.GetString(TokenAlphabet, 8)}";
                var exists = await _context.Payments.AnyAsync(p => p.SystemReceiptNumber == candidate, cancellationToken);
                if (!exists)
                {
                    return candidate;
                }
            }

            return $"RH-RCPT-{DateTimeOffset.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..32].ToUpperInvariant();
        }

        private async Task<string> GenerateVerificationCodeAsync(CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var candidate = RandomNumberGenerator.GetString(TokenAlphabet, 24);
                var exists = await _context.Payments.AnyAsync(p => p.ReceiptVerificationCode == candidate, cancellationToken);
                if (!exists)
                {
                    return candidate;
                }
            }

            return Guid.NewGuid().ToString("N");
        }

        private string BuildVerificationUrl(string verificationCode)
        {
            if (string.IsNullOrWhiteSpace(verificationCode))
            {
                return string.Empty;
            }

            var portalBaseUrl = _configuration["Portal:BaseUrl"]?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(portalBaseUrl))
            {
                portalBaseUrl = "https://localhost:7059";
            }

            return $"{portalBaseUrl}/Receipts/Verify/{Uri.EscapeDataString(verificationCode)}";
        }

        private static string BuildPeriodLabel(IReadOnlyCollection<RentPeriod> periods)
        {
            if (periods.Count == 0)
            {
                return "Rent payment";
            }

            var first = periods.OrderBy(period => period.PeriodStart).First();
            var last = periods.OrderBy(period => period.PeriodStart).Last();
            return periods.Count == 1
                ? $"{first.PeriodStart:MMM d, yyyy} - {first.PeriodEnd:MMM d, yyyy}"
                : $"{first.PeriodStart:MMM d, yyyy} - {last.PeriodEnd:MMM d, yyyy}";
        }

        private static string BuildTenantReceiptEmail(RentReceiptDto receipt)
        {
            return $"""
                Your rent payment receipt is ready.

                Receipt: {receipt.ReceiptNumber}
                Property: {receipt.PropertyName}
                Apartment: {receipt.ApartmentName}
                Period: {receipt.PeriodLabel}
                Amount: {receipt.Amount:N0} {receipt.Currency}
                Payment date: {receipt.PaymentDate:MMM d, yyyy}

                View and verify your receipt:
                {receipt.VerificationUrl}
                """;
        }

        private static string BuildLandlordPaymentEmail(RentReceiptDto receipt)
        {
            return $"""
                A rent payment was recorded.

                Tenant: {receipt.TenantName}
                Property: {receipt.PropertyName}
                Apartment: {receipt.ApartmentName}
                Period: {receipt.PeriodLabel}
                Amount: {receipt.Amount:N0} {receipt.Currency}
                Method: {receipt.Method}
                Receipt: {receipt.ReceiptNumber}

                Verification link:
                {receipt.VerificationUrl}
                """;
        }

        private static IReadOnlyCollection<EmailAttachment> BuildReceiptAttachment(RentReceiptDto receipt)
        {
            var html = BuildReceiptAttachmentHtml(receipt);
            var safeReceiptNumber = string.Join(
                "-",
                receipt.ReceiptNumber.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

            if (string.IsNullOrWhiteSpace(safeReceiptNumber))
            {
                safeReceiptNumber = "rent-receipt";
            }

            return new[]
            {
                new EmailAttachment
                {
                    FileName = $"{safeReceiptNumber}.html",
                    ContentType = "text/html",
                    Content = Encoding.UTF8.GetBytes(html)
                }
            };
        }

        private static string BuildReceiptAttachmentHtml(RentReceiptDto receipt)
        {
            static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

            var providerReceipt = string.IsNullOrWhiteSpace(receipt.ProviderReceiptUrl)
                ? string.Empty
                : $"""
                    <p><strong>Stripe receipt:</strong> <a href="{E(receipt.ProviderReceiptUrl)}">{E(receipt.ProviderReceiptUrl)}</a></p>
                    """;

            var qrCode = string.IsNullOrWhiteSpace(receipt.QrCodeSvg)
                ? string.Empty
                : $"""<div class="qr">{receipt.QrCodeSvg}</div>""";

            return $$"""
                <!doctype html>
                <html lang="en">
                <head>
                    <meta charset="utf-8">
                    <title>Rent receipt {{E(receipt.ReceiptNumber)}}</title>
                    <style>
                        body { font-family: Arial, sans-serif; color: #102033; margin: 32px; }
                        .receipt { max-width: 820px; margin: 0 auto; border: 1px solid #d9e3ea; padding: 28px; }
                        .head { display: flex; justify-content: space-between; gap: 24px; border-bottom: 2px solid #0f766e; padding-bottom: 18px; }
                        h1 { margin: 0; font-size: 30px; letter-spacing: 0; }
                        .muted { color: #607286; }
                        .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 18px; margin: 24px 0; }
                        .box { background: #f7fafc; border: 1px solid #d9e3ea; padding: 16px; }
                        table { width: 100%; border-collapse: collapse; margin-top: 20px; }
                        th, td { text-align: left; padding: 12px; border-bottom: 1px solid #e5edf2; }
                        th { background: #edf5f5; }
                        .total { font-size: 22px; font-weight: 700; }
                        .verify { display: flex; justify-content: space-between; gap: 24px; margin-top: 28px; align-items: flex-end; }
                        .qr svg { width: 120px; height: 120px; }
                    </style>
                </head>
                <body>
                    <main class="receipt">
                        <section class="head">
                            <div>
                                <p class="muted">Lontsi Homes rent receipt</p>
                                <h1>Receipt</h1>
                            </div>
                            <div>
                                <p><strong>No:</strong> {{E(receipt.ReceiptNumber)}}</p>
                                <p><strong>Issued:</strong> {{receipt.IssuedAt:MMM d, yyyy}}</p>
                                <p><strong>Paid:</strong> {{receipt.PaymentDate:MMM d, yyyy}}</p>
                            </div>
                        </section>

                        <section class="grid">
                            <div class="box">
                                <p class="muted">Tenant</p>
                                <p><strong>{{E(receipt.TenantName)}}</strong></p>
                                <p>{{E(receipt.TenantEmail)}}</p>
                            </div>
                            <div class="box">
                                <p class="muted">Landlord</p>
                                <p><strong>{{E(receipt.LandlordName)}}</strong></p>
                                <p>{{E(receipt.LandlordEmail)}}</p>
                            </div>
                        </section>

                        <table>
                            <thead>
                                <tr>
                                    <th>Property</th>
                                    <th>Apartment</th>
                                    <th>Period</th>
                                    <th>Method</th>
                                    <th>Amount</th>
                                </tr>
                            </thead>
                            <tbody>
                                <tr>
                                    <td>{{E(receipt.PropertyName)}}</td>
                                    <td>{{E(receipt.ApartmentName)}}</td>
                                    <td>{{E(receipt.PeriodLabel)}}</td>
                                    <td>{{E(receipt.Method.ToString())}}</td>
                                    <td class="total">{{receipt.Amount:N0}} {{E(receipt.Currency)}}</td>
                                </tr>
                            </tbody>
                        </table>

                        <section class="verify">
                            <div>
                                <p><strong>Verification stamp:</strong> {{E(receipt.VerificationCode)}}</p>
                                <p><strong>Verify online:</strong> <a href="{{E(receipt.VerificationUrl)}}">{{E(receipt.VerificationUrl)}}</a></p>
                                {{providerReceipt}}
                            </div>
                            {{qrCode}}
                        </section>
                    </main>
                </body>
                </html>
                """;
        }

        private async Task TrySendEmailAsync(
            string to,
            string subject,
            string body,
            IReadOnlyCollection<EmailAttachment> attachments,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _emailService.SendEmailAsync(to, subject, body, attachments);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Receipt email failed for {Recipient}.", to);
            }
        }
    }
}
