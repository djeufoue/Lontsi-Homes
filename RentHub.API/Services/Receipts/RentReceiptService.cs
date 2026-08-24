using System.Security.Cryptography;
using System.IO;
using Common.CommunicationModels;
using Common.Enums;
using Common.Helpers;
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
                Method = payment.Method,
                PaymentMethod = payment.Method switch
                {
                    PaymentMethodEnum.Cash => "Cash / off-platform (recorded by landlord)",
                    PaymentMethodEnum.Momo => "MTN Mobile Money",
                    PaymentMethodEnum.OrangeMoney => "Orange Money",
                    PaymentMethodEnum.Card => "Card",
                    _ => payment.Method.ToString()
                },
                TenantName = payment.Tenant?.FullName ?? payment.Tenant?.Email ?? "Tenant",
                LandlordName = payment.Landlord?.FullName ?? payment.Landlord?.Email ?? "Landlord",
                PropertyName = payment.Tenancy?.Apartment?.Property?.Name ?? string.Empty,
                ApartmentName = payment.Tenancy?.Apartment?.Name ?? string.Empty,
                PeriodLabel = BuildPeriodLabel(periods),
                PeriodStart = periods.FirstOrDefault()?.PeriodStart,
                PeriodEnd = periods.LastOrDefault()?.PeriodEnd,
                Lines = BuildReceiptLines(periods)
            };
        }

        public async Task SendReceiptNotificationsAsync(
            RentReceiptDto receipt,
            bool notifyTenant,
            bool notifyLandlord,
            CancellationToken cancellationToken = default)
        {
            if (notifyTenant && !string.IsNullOrWhiteSpace(receipt.TenantEmail))
            {
                var isFrench = receipt.TenantEmailLanguage == PlatformLanguage.French;
                await TrySendEmailAsync(
                    receipt.TenantEmail,
                    isFrench ? $"Facture de loyer {receipt.ReceiptNumber}" : $"Rent invoice {receipt.ReceiptNumber}",
                    BuildTenantReceiptEmail(receipt, receipt.TenantEmailLanguage),
                    BuildReceiptAttachment(receipt, receipt.TenantEmailLanguage),
                    cancellationToken);
            }

            if (notifyLandlord && !string.IsNullOrWhiteSpace(receipt.LandlordEmail))
            {
                var isFrench = receipt.LandlordEmailLanguage == PlatformLanguage.French;
                await TrySendEmailAsync(
                    receipt.LandlordEmail,
                    isFrench ? $"Facture de loyer - paiement reçu - {receipt.ReceiptNumber}" : $"Rent invoice - payment received - {receipt.ReceiptNumber}",
                    BuildLandlordPaymentEmail(receipt, receipt.LandlordEmailLanguage),
                    BuildReceiptAttachment(receipt, receipt.LandlordEmailLanguage),
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
                TenantPhone = payment.Tenant?.PhoneNumber ?? string.Empty,
                TenantEmailLanguage = payment.Tenant?.EmailLanguage ?? PlatformLanguage.English,
                LandlordName = payment.Landlord?.FullName ?? payment.Landlord?.Email ?? "Landlord",
                LandlordEmail = payment.Landlord?.Email ?? string.Empty,
                LandlordEmailLanguage = payment.Landlord?.EmailLanguage ?? PlatformLanguage.English,
                PropertyName = payment.Tenancy?.Apartment?.Property?.Name ?? string.Empty,
                ApartmentName = payment.Tenancy?.Apartment?.Name ?? string.Empty,
                PeriodLabel = BuildPeriodLabel(periods),
                PeriodStart = periods.FirstOrDefault()?.PeriodStart,
                PeriodEnd = periods.LastOrDefault()?.PeriodEnd,
                Lines = BuildReceiptLines(periods),
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

        private static List<RentReceiptLineDto> BuildReceiptLines(IEnumerable<RentPeriod> periods)
        {
            return periods
                .OrderBy(period => period.PeriodStart)
                .Select(period => new RentReceiptLineDto
                {
                    RentPeriodId = period.Id,
                    PeriodStart = period.PeriodStart,
                    PeriodEnd = period.PeriodEnd,
                    PeriodAmount = period.Amount,
                    PaidAmount = period.PaidAmount
                })
                .ToList();
        }

        private static string BuildTenantReceiptEmail(RentReceiptDto receipt, PlatformLanguage language)
        {
            if (language == PlatformLanguage.French)
            {
                return $"""
                    Votre facture de loyer est prête.

                    Facture : {receipt.ReceiptNumber}
                    Propriété : {receipt.PropertyName}
                    Appartement : {receipt.ApartmentName}
                    Période : {FormatReceiptPeriod(receipt, language)}
                    Détail :
                    {FormatReceiptLines(receipt, language)}
                    Montant : {FormatReceiptAmount(receipt, language)}
                    Date du paiement : {receipt.PaymentDate.ToString("d MMM yyyy", System.Globalization.CultureInfo.GetCultureInfo(language.ToCultureName()))}

                    Consulter et vérifier votre facture :
                    {receipt.VerificationUrl}
                    """;
            }

            return $"""
                Your rent invoice is ready.

                Invoice: {receipt.ReceiptNumber}
                Property: {receipt.PropertyName}
                Apartment: {receipt.ApartmentName}
                Period: {FormatReceiptPeriod(receipt, language)}
                Details:
                {FormatReceiptLines(receipt, language)}
                Amount: {FormatReceiptAmount(receipt, language)}
                Payment date: {receipt.PaymentDate.ToString("MMM d, yyyy", System.Globalization.CultureInfo.GetCultureInfo(language.ToCultureName()))}

                View and verify your invoice:
                {receipt.VerificationUrl}
                """;
        }

        private static string BuildLandlordPaymentEmail(RentReceiptDto receipt, PlatformLanguage language)
        {
            if (language == PlatformLanguage.French)
            {
                return $"""
                    Un paiement de loyer a été enregistré.

                    Locataire : {receipt.TenantName}
                    Propriété : {receipt.PropertyName}
                    Appartement : {receipt.ApartmentName}
                    Période : {FormatReceiptPeriod(receipt, language)}
                    Détail :
                    {FormatReceiptLines(receipt, language)}
                    Montant : {FormatReceiptAmount(receipt, language)}
                    Mode : {FormatPaymentMethod(receipt.Method, language)}
                    Facture : {receipt.ReceiptNumber}

                    Lien de vérification :
                    {receipt.VerificationUrl}
                    """;
            }

            return $"""
                A rent payment was recorded.

                Tenant: {receipt.TenantName}
                Property: {receipt.PropertyName}
                Apartment: {receipt.ApartmentName}
                Period: {FormatReceiptPeriod(receipt, language)}
                Details:
                {FormatReceiptLines(receipt, language)}
                Amount: {FormatReceiptAmount(receipt, language)}
                Method: {FormatPaymentMethod(receipt.Method, language)}
                Invoice: {receipt.ReceiptNumber}

                Verification link:
                {receipt.VerificationUrl}
                """;
        }

        private static IReadOnlyCollection<EmailAttachment> BuildReceiptAttachment(RentReceiptDto receipt, PlatformLanguage language)
        {
            var pdf = RentReceiptPdfBuilder.Build(receipt, language);
            var safeReceiptNumber = string.Join(
                "-",
                receipt.ReceiptNumber.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

            if (string.IsNullOrWhiteSpace(safeReceiptNumber))
            {
                safeReceiptNumber = "rent-invoice";
            }

            return new[]
            {
                new EmailAttachment
                {
                    FileName = $"{safeReceiptNumber}.pdf",
                    ContentType = "application/pdf",
                    Content = pdf
                }
            };
        }

        private static string FormatReceiptPeriod(RentReceiptDto receipt, PlatformLanguage language)
        {
            if (!receipt.PeriodStart.HasValue || !receipt.PeriodEnd.HasValue)
            {
                return receipt.PeriodLabel;
            }

            var culture = System.Globalization.CultureInfo.GetCultureInfo(language.ToCultureName());
            return $"{receipt.PeriodStart.Value.ToString("d MMM yyyy", culture)} - {receipt.PeriodEnd.Value.ToString("d MMM yyyy", culture)}";
        }

        private static string FormatReceiptAmount(RentReceiptDto receipt, PlatformLanguage language)
            => $"{receipt.Amount.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo(language.ToCultureName()))} {receipt.Currency}";

        private static string FormatReceiptLines(RentReceiptDto receipt, PlatformLanguage language)
        {
            if (receipt.Lines.Count == 0) return $"- {FormatReceiptPeriod(receipt, language)}: {FormatReceiptAmount(receipt, language)}";
            var culture = System.Globalization.CultureInfo.GetCultureInfo(language.ToCultureName());
            return string.Join(
                Environment.NewLine,
                receipt.Lines.Select(line =>
                    $"- {line.PeriodStart.ToString("d MMM yyyy", culture)} - {line.PeriodEnd.ToString("d MMM yyyy", culture)}: {line.PaidAmount.ToString("N0", culture)} {receipt.Currency}"));
        }

        private static string FormatPaymentMethod(PaymentMethodEnum method, PlatformLanguage language)
        {
            if (language != PlatformLanguage.French)
            {
                return method.ToString();
            }

            return method switch
            {
                PaymentMethodEnum.Cash => "Espèces / hors plateforme",
                PaymentMethodEnum.Card => "Carte",
                PaymentMethodEnum.Momo => "MTN Mobile Money",
                PaymentMethodEnum.OrangeMoney => "Orange Money",
                _ => method.ToString()
            };
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
