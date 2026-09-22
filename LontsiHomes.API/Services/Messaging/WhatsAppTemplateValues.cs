using System.Globalization;
using Common.CommunicationModels;
using Common.Enums;
using LontsiHomes.API.Models.Entities;

namespace LontsiHomes.API.Services.Messaging;

/// <summary>Positional values follow the provider template text, not the email layout.</summary>
public static class WhatsAppTemplateValues
{
    // Configure only the dynamic suffix expected by the approved button, not a guessed full URL.
    public static string[]? UrlButton(IConfiguration configuration, string eventType,
        string variable, string value)
    {
        var pattern = configuration[$"Infobip:UrlButtonParameters:{eventType}"];
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        var result = pattern.Replace("{" + variable + "}", Uri.EscapeDataString(value), StringComparison.Ordinal);
        return result.Contains('{') || result.Contains('}') ? null : new[] { result };
    }

    public static string Date(DateTimeOffset value, PlatformLanguage language) =>
        value.ToString("dd MMM yyyy", CultureInfo.GetCultureInfo(language.ToCultureName()));

    public static string Amount(decimal value, PlatformLanguage language) =>
        value.ToString("0.##", CultureInfo.GetCultureInfo(language.ToCultureName()));

    public static string[] Receipt(RentReceiptDto receipt) => new[]
    {
        receipt.TenantName, receipt.PropertyName, receipt.ApartmentName,
        Amount(receipt.Amount, receipt.TenantEmailLanguage), receipt.Currency,
        Date(receipt.PaymentDate, receipt.TenantEmailLanguage), receipt.ReceiptNumber
    };

    public static string[] Reminder(string name, string property, string apartment,
        RentReminderCategoryEnum category, IEnumerable<RentReminderPeriod> periods,
        DateTimeOffset fallbackDueDate, decimal amount, string currency, PlatformLanguage language)
    {
        var snapshots = periods.OrderBy(p => p.PeriodStartSnapshot).ToArray();
        var dueDate = snapshots.Where(p => p.IsTrigger).Select(p => (DateTimeOffset?)p.DueDateSnapshot)
            .Min() ?? fallbackDueDate;
        // Overdue {{4}} is a period, unlike the due-date variable in upcoming/today templates.
        var period = string.Join(" ; ", snapshots.Where(p => p.OutstandingAmountSnapshot > 0)
            .Select(p => $"{Date(p.PeriodStartSnapshot, language)} – {Date(p.PeriodEndSnapshot, language)}")
            .Distinct());
        return new[] { name, property, apartment,
            category == RentReminderCategoryEnum.AfterDue ? period : Date(dueDate, language),
            Amount(amount, language), currency };
    }

    public static string RequestType(string type, PlatformLanguage language) => language == PlatformLanguage.French
        ? type switch { "renewal" => "renouvellement", "termination" => "fin de bail", _ => type }
        : type;

    public static string RequestStatus(string status, PlatformLanguage language) => language == PlatformLanguage.French
        ? status switch { "approved" => "approuvée", "rejected" => "refusée", "cancelled" => "annulée", _ => status }
        : status;
}
