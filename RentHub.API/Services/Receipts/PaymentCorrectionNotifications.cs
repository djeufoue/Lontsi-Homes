using System.Globalization;
using System.Text.Json;
using Common.CommunicationModels;
using Common.Enums;
using Common.Helpers;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Data;
using RentHub.API.Services.Email;

namespace RentHub.API.Services.Receipts;

// The audit is stored in the same transaction as the correction. A server restart
// or SMTP failure cannot lose the notification; each recipient is retried separately.
public sealed class PaymentCorrectionNotifications
{
    private readonly ApplicationDbContext _context;
    private readonly IEmailService _email;
    private readonly ILogger<PaymentCorrectionNotifications> _logger;
    public PaymentCorrectionNotifications(ApplicationDbContext context, IEmailService email, ILogger<PaymentCorrectionNotifications> logger)
    { _context = context; _email = email; _logger = logger; }

    [DisableConcurrentExecution(120)]
    public async Task ProcessAsync()
    {
        var pending = await _context.Payments.Where(p => p.CorrectionJson != null &&
            (p.CorrectionTenantNotifiedAt == null || p.CorrectionLandlordNotifiedAt == null))
            .OrderBy(p => p.UpdatedAt).Take(20).ToListAsync();
        foreach (var payment in pending)
        {
            try
            {
                var audit = JsonSerializer.Deserialize<ManualPaymentCorrection>(payment.CorrectionJson!)!;
                if (payment.CorrectionTenantNotifiedAt == null && await SendAsync(audit, true))
                {
                    payment.CorrectionTenantNotifiedAt = DateTimeOffset.UtcNow;
                    await _context.SaveChangesAsync();
                }
                if (payment.CorrectionLandlordNotifiedAt == null && await SendAsync(audit, false))
                {
                    payment.CorrectionLandlordNotifiedAt = DateTimeOffset.UtcNow;
                    await _context.SaveChangesAsync();
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "Receipt correction notification failed for payment {PaymentId}", payment.Id); }
        }
    }

    private async Task<bool> SendAsync(ManualPaymentCorrection audit, bool tenant)
    {
        var original = audit.OriginalReceipt;
        var to = tenant ? original.TenantEmail : original.LandlordEmail;
        if (string.IsNullOrWhiteSpace(to)) return false;
        var language = tenant ? original.TenantEmailLanguage : original.LandlordEmailLanguage;
        var fr = language == PlatformLanguage.French;
        var culture = CultureInfo.GetCultureInfo(language.ToCultureName());
        var replacement = audit.ReplacementReceipt;
        var removed = original.Amount - (replacement?.Amount ?? 0);
        var periods = original.Lines.Where(line => audit.ReleasedPeriodIds.Contains(line.RentPeriodId))
            .Select(line => $"{line.PeriodStart.ToString("dd MMM yyyy", culture)} - {line.PeriodEnd.ToString("dd MMM yyyy", culture)}");
        var body = fr
            ? $"Rectification d'un enregistrement de paiement de loyer.\nLa facture {original.ReceiptNumber} est annulée et ne doit plus être utilisée comme preuve de paiement.\nPériodes remises à non payé : {string.Join(", ", periods)}.\nMontant retiré de l'enregistrement : {removed.ToString("N0", culture)} {original.Currency}.\nMotif : {audit.Reason}\nIl ne s'agit pas d'un remboursement : aucun mouvement d'argent n'a été effectué par cette correction.\n"
            : $"Correction of a recorded rent payment.\nInvoice {original.ReceiptNumber} is cancelled and must no longer be used as proof of payment.\nPeriods reset to unpaid: {string.Join(", ", periods)}.\nAmount removed from the record: {removed.ToString("N0", culture)} {original.Currency}.\nReason: {audit.Reason}\nThis is not a refund: this correction did not transfer any money.\n";
        if (replacement != null)
            body += fr
                ? $"Les autres périodes restent payées. Facture de remplacement : {replacement.ReceiptNumber}, {replacement.Amount.ToString("N0", culture)} {replacement.Currency}.\n{replacement.VerificationUrl}\n"
                : $"The other periods remain paid. Replacement invoice: {replacement.ReceiptNumber}, {replacement.Amount.ToString("N0", culture)} {replacement.Currency}.\n{replacement.VerificationUrl}\n";
        body += fr ? $"Vérification de l'ancienne facture : {original.VerificationUrl}" : $"Original invoice verification: {original.VerificationUrl}";
        // If the replacement has since been corrected too, do not attach an obsolete
        // success PDF. Its verification link leads to the next correction instead.
        var attachments = new List<EmailAttachment>();
        if (replacement != null && await _context.Payments.AnyAsync(p => p.Id == replacement.PaymentId && !p.IsDeleted &&
            p.Status == PaymentStatusEnum.Success && p.CorrectionJson == null))
            attachments.Add(new EmailAttachment { FileName = $"receipt-{replacement.PaymentId}.pdf", ContentType = "application/pdf",
                Content = RentReceiptPdfBuilder.Build(replacement, language) });
        var result = await _email.TrySendEmailAsync(new EmailMessage { To = to,
            Subject = (fr ? "Rectification de facture de loyer - " : "Rent invoice correction - ") + original.ReceiptNumber,
            PlainTextBody = body, Attachments = attachments });
        if (!result.Succeeded) _logger.LogWarning("Correction email attempt failed for invoice {Invoice}", original.ReceiptNumber);
        return result.Succeeded;
    }
}
