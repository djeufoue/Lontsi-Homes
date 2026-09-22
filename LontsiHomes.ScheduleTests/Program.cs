using Common.CommunicationModels;
using Common.Enums;
using Common.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using LontsiHomes.API.Data;
using LontsiHomes.API.Helpers;
using LontsiHomes.API.Models.Entities;
using LontsiHomes.API.Models.Settings;
using LontsiHomes.API.Services.Messaging;
using LontsiHomes.API.Services.Otp;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using LontsiHomes.API.Services.Receipts;

var tests = new (string Name, Action Run)[]
{
    ("Cycle starts on days 1, 8 and 15", TestOrdinaryAnniversaries),
    ("Cycles 29, 30 and 31 do not drift", TestShortMonthAnniversaries),
    ("Leap-year anniversary", TestLeapYear),
    ("Payment intervals 1, 2, 3, 6 and 12", TestPaymentIntervals),
    ("Fixed quarterly tenancy and shortened final group", TestFixedTermFinalGroup),
    ("Tracking start omits covered history", TestTrackingStart),
    ("Up-to-date monthly tenancy provisions the next payment group", TestUpToDateMonthlyGroup),
    ("Open tenancy provisions current and next complete payment groups", TestOpenEndedPaymentGroups),
    ("Quarterly balances are 90k, 60k, 30k and zero", TestQuarterlyBalances),
    ("Tenant payment selects the complete oldest outstanding group", TestTenantPaymentGroupSelection),
    ("Billing-group identity does not depend on payment status", TestStableGroupIdentity),
    ("One itemized PDF receipt covers multiple periods", TestItemizedReceiptPdf),
    ("Receipt layout has structured sections and paginated period details", TestReceiptLayout),
    ("Apartment floors accept non-negative values without an artificial upper limit", TestApartmentFloorValidation),
    ("New managers can edit apartments by default", TestManagerDefaultPermission),
    ("Phone inputs remove all whitespace before validation", TestPhoneNumberNormalization),
    ("WhatsApp numbers are normalized to E.164", TestWhatsAppE164Normalization),
    ("WhatsApp is optional and invited numbers remain proposals", TestWhatsAppPreferenceInputs),
    ("Admin WhatsApp activation distinguishes missing, pending, expired, active and withdrawn consent", TestAdminWhatsAppActivation),
    ("WhatsApp template registry is explicit and fail-closed", TestWhatsAppTemplateRegistry),
    ("Infobip JSON template approvals bind correctly with configuration overrides", TestInfobipTemplateConfiguration),
    ("WhatsApp inbound policy handles opt-out words only", TestWhatsAppInboundPolicy),
    ("Infobip inbound payload preserves STOP text and sender together", TestInfobipInboundPayload),
    ("WhatsApp uniqueness and delivery idempotency are enforced by the model", TestWhatsAppPersistenceModel),
    ("OTP codes are hashed and verified", TestOtpHashing),
    ("Main-phone onboarding verification can be paused without bypassing other checks", TestMainPhoneVerificationPause),
    ("WhatsApp replacement preserves the old destination until confirmation", TestWhatsAppNumberChange),
    ("An already active WhatsApp number does not need another OTP", TestWhatsAppUnchangedNumber),
    ("Infobip SMS client sends the expected structured request", () => TestInfobipSmsClient().GetAwaiter().GetResult()),
    ("Infobip WhatsApp client permits approved templates only", () => TestInfobipWhatsAppClient().GetAwaiter().GetResult()),
    ("Screenshot template contracts match body, buttons and document header", TestScreenshotTemplateContracts),
    ("WhatsApp values match receipt and reminder variables in both languages", TestWhatsAppVariableMappings),
    ("Receipt media tokens expire and cannot authorize other receipts", () => TestReceiptMedia().GetAwaiter().GetResult()),
    ("WhatsApp outbox preserves documents and reads legacy arrays", TestWhatsAppDocumentOutbox),
    ("Infobip receipt document payload omits buttons and rejects invalid headers", () => TestWhatsAppDocumentClient().GetAwaiter().GetResult()),
    ("French and English decimal amounts parse identically", TestFlexibleDecimalAmounts),
    ("Production migration repairs partially-applied columns", TestProductionMigrationGuards),
    ("WhatsApp production migration has guarded legacy changes and idempotent SQL", TestWhatsAppProductionMigration),
    ("Manual payment correction migration preserves existing payments", TestManualPaymentCorrectionMigration),
    ("EF migration snapshot matches the current model", TestMigrationModel)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {test.Name}: {exception.Message}");
        Console.Error.WriteLine(failures[^1]);
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} schedule test(s) failed.");
    return 1;
}

if (args.Length == 2 && args[0] == "--receipt-preview")
{
    Directory.CreateDirectory(args[1]);
    var preview = ReceiptLayoutFixture();
    File.WriteAllBytes(Path.Combine(args[1], "receipt-fr.pdf"), RentReceiptPdfBuilder.Build(preview, PlatformLanguage.French));
    File.WriteAllBytes(Path.Combine(args[1], "receipt-en.pdf"), RentReceiptPdfBuilder.Build(preview, PlatformLanguage.English));
    preview.Lines = Enumerable.Range(0, 32).Select(index => new RentReceiptLineDto
    {
        PeriodStart = Utc(2024, 1, 15).AddMonths(index),
        PeriodEnd = Utc(2024, 1, 15).AddMonths(index + 1).AddDays(-1),
        PeriodAmount = 150_000, PaidAmount = 150_000
    }).ToList();
    preview.Amount = preview.Lines.Sum(line => line.PaidAmount);
    File.WriteAllBytes(Path.Combine(args[1], "receipt-multiple.pdf"), RentReceiptPdfBuilder.Build(preview, PlatformLanguage.French));
}

Console.WriteLine($"All {tests.Length} rent-schedule tests passed.");
return 0;

static void TestOrdinaryAnniversaries()
{
    foreach (var day in new[] { 1, 8, 15 })
    {
        var start = Utc(2024, 9, day);
        var periods = Fixed(start, Utc(2024, 12, Math.Min(day, 20)), 1);
        Equal(start.Date, periods[0].PeriodStart.Date, $"first boundary for day {day}");
        Equal(RentPeriodScheduleHelper.MonthlyBoundary(start, 1).AddDays(-1).Date, periods[0].PeriodEnd.Date, $"first end for day {day}");
        Equal(RentPeriodScheduleHelper.MonthlyBoundary(start, 1).Date, periods[1].PeriodStart.Date, $"second boundary for day {day}");
    }
}

static void TestShortMonthAnniversaries()
{
    var start31 = Utc(2025, 1, 31);
    Equal(Utc(2025, 2, 28).Date, RentPeriodScheduleHelper.MonthlyBoundary(start31, 1).Date, "February boundary");
    Equal(Utc(2025, 3, 31).Date, RentPeriodScheduleHelper.MonthlyBoundary(start31, 2).Date, "March restores day 31");
    Equal(Utc(2025, 4, 30).Date, RentPeriodScheduleHelper.MonthlyBoundary(start31, 3).Date, "April clamps day 31");

    foreach (var day in new[] { 29, 30 })
    {
        var start = Utc(2025, 1, day);
        Equal(day, RentPeriodScheduleHelper.MonthlyBoundary(start, 2).Day, $"March restores day {day}");
    }
}

static void TestLeapYear()
{
    var start = Utc(2024, 1, 31);
    Equal(Utc(2024, 2, 29).Date, RentPeriodScheduleHelper.MonthlyBoundary(start, 1).Date, "leap February");
    Equal(Utc(2024, 3, 31).Date, RentPeriodScheduleHelper.MonthlyBoundary(start, 2).Date, "post-leap restoration");
}

static void TestPaymentIntervals()
{
    foreach (var interval in new[] { 1, 2, 3, 6, 12 })
    {
        var periods = Fixed(Utc(2024, 9, 15), Utc(2025, 9, 14), interval);
        var firstGroup = periods.Where(period => period.BillingGroupSequence == 0).ToList();
        Equal(interval, firstGroup.Count, $"group size {interval}");
        Equal(1, firstGroup.Select(period => period.DueDate).Distinct().Count(), $"one due date for interval {interval}");
    }
}

static void TestFixedTermFinalGroup()
{
    var end = Utc(2025, 1, 20);
    var periods = Fixed(Utc(2024, 9, 15), end, 3);
    Equal(end.Date, periods[^1].PeriodEnd.Date, "final period ends with tenancy");
    True(periods.All(period => period.PeriodEnd.Date <= end.Date), "no period exceeds tenancy end");
    Equal(3, periods.Count(period => period.BillingGroupSequence == 0), "complete first quarter");
    Equal(2, periods.Count(period => period.BillingGroupSequence == 1), "final quarter is incomplete");
}

static void TestTrackingStart()
{
    var tenancyStart = Utc(2024, 9, 15);
    var trackingStart = Utc(2026, 8, 15);
    var periods = RentPeriodScheduleHelper.GeneratePeriods(
        tenancyStart, null, TenancyEndBehaviorEnum.NoEndDate, 30_000, 15,
        Utc(2026, 8, 23), 3, trackingStart);
    Equal(trackingStart.Date, periods[0].PeriodStart.Date, "first tracked period");
    True(periods.All(period => period.PeriodStart.Date >= trackingStart.Date), "covered history omitted");
    Equal(4, periods.Count, "partial current quarter plus one complete future quarter");
    Equal(3, periods.Count(period => period.BillingGroupSequence == 8), "next quarterly group is complete");
}

static void TestOpenEndedPaymentGroups()
{
    var periods = RentPeriodScheduleHelper.GeneratePeriods(
        Utc(2026, 8, 15), null, TenancyEndBehaviorEnum.NoEndDate, 30_000, 15,
        Utc(2026, 8, 23), 6, Utc(2026, 8, 15));
    Equal(12, periods.Count, "current and next semiannual groups are provisioned");
    Equal(6, periods.Count(period => period.BillingGroupSequence == 0), "first semiannual group complete");
    Equal(6, periods.Count(period => period.BillingGroupSequence == 1), "next semiannual group complete");
}

static void TestUpToDateMonthlyGroup()
{
    var start = Utc(2024, 9, 15);
    var now = Utc(2026, 8, 23);
    var trackingStart = RentPeriodScheduleHelper.ResolveNextBillingGroupStart(start, now, 1);
    var periods = RentPeriodScheduleHelper.GeneratePeriods(
        start, null, TenancyEndBehaviorEnum.NoEndDate, 30_000, 15,
        now, 1, trackingStart);
    Equal(1, periods.Count, "one future monthly period");
    Equal(trackingStart.Date, periods[0].PeriodStart.Date, "next monthly group starts tracking");
}

static void TestQuarterlyBalances()
{
    var group = Fixed(Utc(2024, 9, 15), Utc(2024, 12, 14), 3).Take(3).ToList();
    Equal(90_000m, Balance(group), "initial balance");
    group[0].PaidAmount = 30_000;
    Equal(60_000m, Balance(group), "after one month");
    group[1].PaidAmount = 30_000;
    Equal(30_000m, Balance(group), "after two months");
    group[2].PaidAmount = 30_000;
    Equal(0m, Balance(group), "fully paid");
}

static void TestTenantPaymentGroupSelection()
{
    var periods = Fixed(Utc(2024, 9, 15), Utc(2025, 3, 14), 3)
        .Select((period, index) => new RentPeriod
        {
            Id = index + 1,
            PeriodStart = period.PeriodStart,
            PeriodEnd = period.PeriodEnd,
            DueDate = period.DueDate,
            BillingGroupSequence = period.BillingGroupSequence,
            Amount = period.Amount,
            PaidAmount = period.PaidAmount,
            Status = period.Status
        })
        .ToList();

    var firstGroup = RentPaymentGroupHelper.SelectOldestOutstandingGroup(periods);
    Equal(3, firstGroup.Count, "complete first quarter selected");
    Equal(90_000m, Balance(firstGroup.Select(period => new RentPeriodSeedDto
    {
        Amount = period.Amount,
        PaidAmount = period.PaidAmount
    })), "complete first-quarter balance");

    periods[0].Status = RentPeriodStatusEnum.Paid;
    periods[0].PaidAmount = periods[0].Amount;
    var remainingFirstGroup = RentPaymentGroupHelper.SelectOldestOutstandingGroup(periods);
    Equal(2, remainingFirstGroup.Count, "only unpaid periods in the oldest group selected");

    periods[1].Status = RentPeriodStatusEnum.Paid;
    periods[2].Status = RentPeriodStatusEnum.Paid;
    var nextGroup = RentPaymentGroupHelper.SelectOldestOutstandingGroup(periods);
    Equal(3, nextGroup.Count, "next complete quarter selected after the first is settled");
    Equal(1, nextGroup.Select(period => period.BillingGroupSequence).Distinct().Count(), "selection never crosses billing groups");
}

static void TestStableGroupIdentity()
{
    var periods = Fixed(Utc(2024, 9, 15), Utc(2025, 3, 14), 3);
    var identities = periods.Select(period => period.BillingGroupSequence).ToArray();
    periods[0].Status = RentPeriodStatusEnum.Paid;
    periods[1].Status = RentPeriodStatusEnum.Overdue;
    periods[2].Status = RentPeriodStatusEnum.NotDueYet;
    True(identities.SequenceEqual(periods.Select(period => period.BillingGroupSequence)), "payment status changed a group identity");
}

static void TestItemizedReceiptPdf()
{
    var receipt = new RentReceiptDto
    {
        ReceiptNumber = "RH-RCPT-TEST",
        IssuedAt = Utc(2026, 8, 23),
        PaymentDate = Utc(2026, 8, 23),
        Amount = 60_000,
        Currency = "XAF",
        Method = PaymentMethodEnum.Cash,
        TenantName = "Test Tenant",
        Lines = new List<RentReceiptLineDto>
        {
            new() { PeriodStart = Utc(2026, 8, 15), PeriodEnd = Utc(2026, 9, 14), PeriodAmount = 30_000, PaidAmount = 30_000 },
            new() { PeriodStart = Utc(2026, 9, 15), PeriodEnd = Utc(2026, 10, 14), PeriodAmount = 30_000, PaidAmount = 30_000 }
        }
    };
    var pdf = RentReceiptPdfBuilder.Build(receipt, PlatformLanguage.French);
    True(pdf.Length > 1_000, "PDF was not generated");
    Equal((byte)'%', pdf[0], "PDF signature");
    Equal(2, receipt.Lines.Count, "receipt line count");
    Equal(receipt.Amount, receipt.Lines.Sum(line => line.PaidAmount), "line total");
}

static RentReceiptDto ReceiptLayoutFixture()
{
    // Synthetic preview data only. Generate the same QR format used by the API.
    var qrType = typeof(ApplicationDbContext).Assembly.GetType("LontsiHomes.API.Services.Receipts.SimpleQrCodeGenerator")!;
    var qr = (string)qrType.GetMethod("CreateSvg")!.Invoke(null, new object[] { "https://example.com/receipt/demo", 5 })!;
    return new RentReceiptDto
    {
        ReceiptNumber = "REC-2026-000123", IssuedAt = Utc(2026, 9, 15).AddHours(12), PaymentDate = Utc(2026, 9, 15).AddHours(12),
        Amount = 150_000, Currency = "XAF", Method = PaymentMethodEnum.Cash, Status = PaymentStatusEnum.Success,
        TenantName = "Jean Dupont", TenantEmail = "jean@example.com", TenantPhone = "+237699000001",
        PropertyName = "Residence Les Palmiers", ApartmentName = "Appartement A-12",
        LandlordName = "Marie Martin", LandlordEmail = "marie@example.com", QrCodeSvg = qr,
        PeriodStart = Utc(2026, 8, 15), PeriodEnd = Utc(2026, 9, 14),
        Lines = new List<RentReceiptLineDto> { new() { PeriodStart = Utc(2026, 8, 15), PeriodEnd = Utc(2026, 9, 14), PeriodAmount = 150_000, PaidAmount = 150_000 } }
    };
}

static void TestReceiptLayout()
{
    var receipt = ReceiptLayoutFixture();
    foreach (var language in new[] { PlatformLanguage.French, PlatformLanguage.English })
    {
        var content = Encoding.ASCII.GetString(RentReceiptPdfBuilder.Build(receipt, language));
        var rentalHeading = language == PlatformLanguage.French ? "Informations de location" : "Rental information";
        var totalHeading = language == PlatformLanguage.French ? "Total paye" : "Total paid";
        True(content.Contains(rentalHeading) && content.Contains(totalHeading), "receipt sections must be rendered");
        True(content.IndexOf(receipt.ReceiptNumber, StringComparison.Ordinal) < content.IndexOf(rentalHeading, StringComparison.Ordinal), "summary comes first");
        True(content.IndexOf(rentalHeading, StringComparison.Ordinal) < content.IndexOf(totalHeading, StringComparison.Ordinal), "rental information precedes total");
        True(content.Contains("/Count 1") && content.Contains("VERIFICATION"), "single receipt must retain footer on one page");
        True(content.Contains("0.067 0.094 0.153 rg"), "QR modules must be drawn");
    }
    receipt.Lines = Enumerable.Range(0, 32).Select(index => new RentReceiptLineDto
    {
        PeriodStart = Utc(2024, 1, 15).AddMonths(index), PeriodEnd = Utc(2024, 1, 15).AddMonths(index + 1).AddDays(-1),
        PeriodAmount = 150_000, PaidAmount = 150_000
    }).ToList();
    receipt.Amount = receipt.Lines.Sum(line => line.PaidAmount);
    var multi = Encoding.ASCII.GetString(RentReceiptPdfBuilder.Build(receipt, PlatformLanguage.English));
    var pageCount = System.Text.RegularExpressions.Regex.Matches(multi, @"/Type /Page /Parent").Count;
    True(pageCount > 1 && multi.Contains($"/Count {pageCount} "), "long receipts must have consistent multipage metadata");
    foreach (var line in receipt.Lines)
    {
        var label = $"{line.PeriodStart.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)} - {line.PeriodEnd.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}";
        Equal(1, System.Text.RegularExpressions.Regex.Matches(multi, System.Text.RegularExpressions.Regex.Escape(label)).Count,
            "each period must appear exactly once across all detail pages");
    }
    receipt.IsCorrected = true;
    receipt.IsValid = false;
    receipt.Status = PaymentStatusEnum.Cancelled;
    var cancelled = Encoding.ASCII.GetString(RentReceiptPdfBuilder.Build(receipt, PlatformLanguage.English));
    Equal(pageCount, System.Text.RegularExpressions.Regex.Matches(cancelled, "CANCELLED - DO NOT USE").Count,
        "every page of a cancelled receipt must carry the cancellation notice");
    True(cancelled.Contains("Original amount") && cancelled.Contains("Cancelled invoice."),
        "cancelled receipts must explain that the original amount is no longer proof of payment");
}

static void TestMigrationModel()
{
    var options = new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=LontsiHomesModelCheck;Trusted_Connection=True;")
        .Options;
    using var context = new ApplicationDbContext(options);
    var migrationsAssembly = context.GetService<IMigrationsAssembly>();
    True(
        migrationsAssembly.Migrations.ContainsKey("20260824023000_RefactorRentSchedulesAndGroupedPayments"),
        "the rent-schedule migration is not discoverable");
    True(
        migrationsAssembly.Migrations.Keys.Any(key => key.EndsWith("_AddApartmentFloorAndEmailSessionInvalidation", StringComparison.Ordinal)),
        "the apartment floor and session invalidation migration is not discoverable");
    True(
        migrationsAssembly.Migrations.ContainsKey("20260829024825_RemoveFutureRentPeriodCount"),
        "the phase-two rent horizon cleanup migration is not discoverable");
    True(
        migrationsAssembly.Migrations.Keys.Any(key => key.EndsWith("_AddInfobipMessagingAndWhatsAppConsent", StringComparison.Ordinal)),
        "the Infobip messaging and WhatsApp consent migration is not discoverable");
    var snapshotModel = migrationsAssembly.ModelSnapshot?.Model
        ?? throw new InvalidOperationException("migration snapshot is missing");
    snapshotModel = context.GetService<IModelRuntimeInitializer>().Initialize(
        snapshotModel,
        designTime: true,
        context.GetService<IDiagnosticsLogger<DbLoggerCategory.Model.Validation>>());
    var currentModel = context.GetService<IDesignTimeModel>().Model;
    var modelDiffer = context.GetService<IMigrationsModelDiffer>();
    True(
        !modelDiffer.HasDifferences(snapshotModel.GetRelationalModel(), currentModel.GetRelationalModel()),
        "the migration snapshot has pending model changes");
}

static void TestManualPaymentCorrectionMigration()
{
    using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=UnusedSqlGeneration;Trusted_Connection=True;").Options);
    const string target = "20260914214102_AddManualPaymentCorrections";
    var assembly = context.GetService<IMigrationsAssembly>();
    True(assembly.Migrations.ContainsKey(target), "correction migration must be discovered at API startup");
    var migration = assembly.CreateMigration(assembly.Migrations[target], context.Database.ProviderName!);
    var columns = migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.AddColumnOperation>().ToArray();
    var expected = new[] { "CorrectionJson", "CorrectionRequestId", "ReplacementPaymentId",
        "CorrectionTenantNotifiedAt", "CorrectionLandlordNotifiedAt" };
    True(columns.Select(column => column.Name).OrderBy(name => name).SequenceEqual(expected.OrderBy(name => name)),
        "migration must add all correction fields");
    True(columns.All(column => column.Table == "Payments" && column.IsNullable && column.DefaultValue == null),
        "existing payments must remain valid without a correction or backfill");
    True(migration.UpOperations.Count == columns.Length + 1 &&
        migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.CreateIndexOperation>()
            .Any(index => index.Table == "Payments" && index.Name == "IX_Payments_CorrectionRequestId" && !index.IsUnique),
        "upgrade must only add nullable columns and a non-unique request index; one correction may affect several payments");
    var sql = context.GetService<IMigrator>().GenerateScript(
        "20260911020456_AddInfobipMessagingAndWhatsAppConsent", target, MigrationsSqlGenerationOptions.Idempotent);
    True(sql.Contains("BEGIN TRANSACTION", StringComparison.Ordinal) && sql.Contains("COMMIT", StringComparison.Ordinal),
        "schema and migration history must be committed together");
    True(sql.Contains("IF NOT EXISTS", StringComparison.Ordinal) && sql.Contains("[__EFMigrationsHistory]", StringComparison.Ordinal),
        "generated deployment script must skip an already-applied migration");
}

static void TestWhatsAppProductionMigration()
{
    using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=UnusedSqlGeneration;Trusted_Connection=True;").Options);
    var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.OrderBy(key => key).ToArray();
    const string target = "20260911020456_AddInfobipMessagingAndWhatsAppConsent";
    var index = Array.IndexOf(migrations, target);
    True(index > 0, "WhatsApp migration discovered");
    var sql = context.GetService<IMigrator>().GenerateScript(migrations[index - 1], target, MigrationsSqlGenerationOptions.Idempotent);
    True(sql.Contains("THROW 51001", StringComparison.Ordinal), "legacy overlength values stop migration");
    True(sql.Contains("COL_LENGTH(N'dbo.PlatformPaymentSettings', N'SkipLandlordPhoneVerification') IS NOT NULL", StringComparison.Ordinal), "obsolete column drop guarded");
    True(sql.Contains("DECLARE @dropConstraintSql nvarchar(max)", StringComparison.Ordinal) &&
         sql.Contains("EXEC sp_executesql @dropConstraintSql;", StringComparison.Ordinal), "constraint drop executes precomputed SQL");
    True(!sql.Contains("EXEC(N'ALTER TABLE [dbo].[PlatformPaymentSettings] DROP CONSTRAINT ' + QUOTENAME", StringComparison.Ordinal),
         "EXEC must not receive a QUOTENAME function expression");
    True(sql.Contains("BEGIN TRANSACTION", StringComparison.Ordinal), "migration is transactional");
    True(sql.Contains("IF NOT EXISTS", StringComparison.Ordinal) && sql.Contains("[__EFMigrationsHistory]", StringComparison.Ordinal), "idempotent history guards");
    True(sql.Contains("CREATE UNIQUE INDEX [IX_AspNetUsers_NormalizedWhatsAppPhoneNumber]", StringComparison.Ordinal), "phone uniqueness index generated");
    True(sql.Contains("CREATE TABLE [UserCommunicationConsents]", StringComparison.Ordinal) &&
         sql.Contains("CREATE TABLE [NotificationDeliveries]", StringComparison.Ordinal), "WhatsApp tables generated");
}

static void TestProductionMigrationGuards()
{
    var options = new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=LontsiHomesModelCheck;Trusted_Connection=True;")
        .Options;
    using var context = new ApplicationDbContext(options);
    var script = context.GetService<IMigrator>().GenerateScript(
        "20260824023000_RefactorRentSchedulesAndGroupedPayments",
        "20260824041238_AddApartmentFloorAndEmailSessionInvalidation");

    True(script.Contains("COL_LENGTH('dbo.AspNetUsers', 'SessionInvalidatedAt') IS NULL", StringComparison.Ordinal),
        "session invalidation column is not protected against a partial deployment");
    True(script.Contains("COL_LENGTH('dbo.Apartments', 'FloorNumber') IS NULL", StringComparison.Ordinal),
        "floor column is not protected against a partial deployment");

    var cleanupScript = context.GetService<IMigrator>().GenerateScript(
        "20260824041238_AddApartmentFloorAndEmailSessionInvalidation",
        "20260829024825_RemoveFutureRentPeriodCount");
    True(cleanupScript.Contains("COL_LENGTH('dbo.Tenancies', 'FutureRentPeriodCount') IS NOT NULL", StringComparison.Ordinal),
        "the obsolete rent horizon column removal is not guarded");
    True(cleanupScript.Contains("[column].[name] = N'FutureRentPeriodCount'", StringComparison.Ordinal),
        "the obsolete rent horizon default constraint is not removed dynamically");
    True(cleanupScript.Contains("EXEC sys.sp_executesql @futureSql", StringComparison.Ordinal),
        "the rent horizon default constraint cleanup does not use valid dynamic SQL");
    True(cleanupScript.Contains("EXEC sys.sp_executesql @legacySql", StringComparison.Ordinal),
        "the legacy auto-extension default constraint cleanup does not use valid dynamic SQL");
    True(cleanupScript.Contains("DROP COLUMN [FutureRentPeriodCount]", StringComparison.Ordinal),
        "the obsolete rent horizon column is not removed");
}

static void TestApartmentFloorValidation()
{
    True(IsValid(new CreateApartmentRequest { PropertyId = 1, Name = "A", FloorNumber = 0 }), "ground floor should be valid");
    True(IsValid(new CreateApartmentRequest { PropertyId = 1, Name = "A", FloorNumber = 30 }), "floor 30 should be valid");
    True(!IsValid(new CreateApartmentRequest { PropertyId = 1, Name = "A", FloorNumber = -1 }), "negative floor should be rejected");
    True(IsValid(new CreateApartmentRequest { PropertyId = 1, Name = "A", FloorNumber = 31 }), "floor 31 should be valid after the upper limit was removed");
}

static void TestManagerDefaultPermission()
{
    var assignment = new PropertyManagerAssignment();
    True(
        (assignment.PermissionFlags & (long)ManagerPermission.EditApartment) != 0,
        "new manager assignments must include EditApartment");
    Equal(PermissionLevelEnum.ReadWrite, assignment.Permission, "new manager legacy permission level");
    True(
        ((long)ManagerPermissionDefaults.Standard & (long)ManagerPermission.EditApartment) != 0,
        "the standard permission profile must include EditApartment");
}

static void TestPhoneNumberNormalization()
{
    var manager = new AddManagerRequest
    {
        Email = "manager@example.com",
        FullName = "Test Manager",
        CountryCode = " + 237 ",
        PhoneNumber = "6 73\u00A097\t45 62"
    };

    Equal("+237", manager.CountryCode!, "manager country code normalization");
    Equal("673974562", manager.PhoneNumber!, "manager phone normalization");
    True(IsValid(manager), "a manager phone containing whitespace should be valid after normalization");

    var tenancyMember = new AddTenancyMemberRequest
    {
        Email = "tenant@example.com",
        CountryCode = "+ 237",
        PhoneNumber = "6 99 00 11 22",
        WhatsAppPhoneNumber = "+237 6 99 00 11 22",
        Role = TenancyMemberRoleEnum.MainTenant
    };

    Equal("+237", tenancyMember.CountryCode!, "tenant country code normalization");
    Equal("699001122", tenancyMember.PhoneNumber!, "tenant phone normalization");
    Equal("+237699001122", tenancyMember.WhatsAppPhoneNumber!, "WhatsApp phone normalization");
    True(IsValid(tenancyMember), "normalized tenancy member phone fields should remain valid");
}

static void TestWhatsAppE164Normalization()
{
    True(
        PhoneNumberHelper.TryNormalizeE164("+237", "6 91 47 83 87", out var cameroon),
        "Cameroon local WhatsApp number should normalize");
    Equal("+237691478387", cameroon, "Cameroon E.164 value");

    True(
        PhoneNumberHelper.TryNormalizeE164(null, "+1 (519) 555-0123", out var canada),
        "international WhatsApp number should normalize");
    Equal("+15195550123", canada, "Canadian E.164 value");

    True(!PhoneNumberHelper.TryNormalizeE164("+237", "123", out _), "short number should be rejected");
    Equal("237691478387", PhoneNumberHelper.ToProviderDigits(cameroon), "Infobip recipient digits");
}

static void TestWhatsAppPreferenceInputs()
{
    var withoutWhatsApp = new AddManagerRequest
    {
        Email = "manager@example.com",
        FullName = "Example Manager",
        CountryCode = "+237",
        PhoneNumber = "691478387"
    };
    True(IsValid(withoutWhatsApp), "WhatsApp must remain optional for an invited manager");
    True(string.IsNullOrWhiteSpace(withoutWhatsApp.WhatsAppPhoneNumber), "declining WhatsApp must not create a number");

    var proposed = new AddOwnerRequest
    {
        Email = "owner@example.com",
        FullName = "Example Owner",
        CountryCode = "+237",
        PhoneNumber = "691478387",
        WhatsAppPhoneNumber = "+237 699 00 11 22"
    };
    Equal("+237699001122", proposed.WhatsAppPhoneNumber!, "invited WhatsApp proposal normalization");
    True(IsValid(proposed), "a separate proposed WhatsApp number should validate");

    True(PhoneNumberHelper.TryNormalizeE164("+237", "691478387", out var primary), "primary number E.164");
    True(PhoneNumberHelper.TryNormalizeE164("+237", "691478387", out var sameWhatsApp), "same WhatsApp number E.164");
    Equal(primary, sameWhatsApp, "same-number option copies the normalized primary number");
}

static void TestAdminWhatsAppActivation()
{
    var now = DateTimeOffset.UtcNow;
    var user = new ApplicationUser();
    AdminWhatsAppActivationDto Status(bool consent = false, bool revoked = false,
        bool code = false, DateTimeOffset? expiry = null)
        => WhatsAppActivationStatus.Build(user, consent, revoked, code, expiry, now);

    Equal("missing", Status().State, "missing number is not verified");
    user.IsWhatsAppPhoneVerified = true;
    True(!Status(true).IsVerified, "a stale verified flag cannot verify a missing number");
    user.IsWhatsAppPhoneVerified = false;
    user.PendingWhatsAppPhoneNumber = "+237699001122";
    Equal("proposed", Status().State, "saved proposal or failed send needs verification");
    Equal("+237699001122", Status().PhoneNumber!, "pending destination is shown");
    Equal("pending", Status(code: true, expiry: now.AddMinutes(10)).State, "valid OTP awaits user");
    Equal("expired", Status(code: true, expiry: now).State, "expiry boundary");
    Equal("expired", Status(code: true).State, "missing expiry is not a usable OTP");
    user.WhatsAppPhoneNumber = user.PendingWhatsAppPhoneNumber;
    user.NormalizedWhatsAppPhoneNumber = user.WhatsAppPhoneNumber;
    user.PendingWhatsAppPhoneNumber = null;
    user.IsWhatsAppPhoneVerified = true;
    user.WhatsAppPhoneVerifiedAt = now;
    Equal("consent-missing", Status().State, "verification alone does not activate notifications");
    Equal("active", Status(consent: true).State, "verified number with consent is active");
    Equal("revoked", Status(revoked: true).State, "withdrawal is distinct from verification");
    True(Status(revoked: true).IsVerified && !Status(revoked: true).HasConsent, "withdrawal retains verification");
    Equal("active", Status(consent: true, revoked: true).State, "renewed consent wins over past revocation");
    user.PendingWhatsAppPhoneNumber = "+237699003344";
    True(!Status(consent: true).IsVerified && !Status(consent: true).HasConsent, "old verification does not activate replacement");
    Equal("proposed", Status(consent: true).State, "replacement requires new verification");
    True(typeof(AdminWhatsAppActivationDto).GetProperties().All(p => p.Name is not "Code" and not "Hash"),
        "admin activation DTO never exposes OTP material");
}

static void TestWhatsAppTemplateRegistry()
{
    var disabledRegistry = new WhatsAppTemplateRegistry(Options.Create(new InfobipOptions()));
    Equal(18, disabledRegistry.All.Count, "nine bilingual template definitions");
    True(
        disabledRegistry.All.All(item => item.LanguageCode is "fr" or "en" or "en_GB"),
        "only approved French and English languages should be registered");
    True(
        disabledRegistry.All.All(item => item.Category is "Utility" or "Authentication"),
        "marketing and free-form templates must not be registered");
    True(!disabledRegistry.TryGet("new_conversation_message", "fr", out _), "Conversations must not have a WhatsApp template");

    var options = new InfobipOptions
    {
        Templates = new Dictionary<string, InfobipTemplateOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["rent_due_today:fr"] = new() { Approved = true }
        }
    };
    var registry = new WhatsAppTemplateRegistry(Options.Create(options));
    True(registry.TryGet("rent_due_today", "fr", out var approved), "French rent-due template lookup");
    True(registry.IsApproved(approved), "explicitly approved template should be enabled");
    True(registry.TryGet("rent_due_today", "en", out var unapproved), "English rent-due template lookup");
    True(!registry.IsApproved(unapproved), "unconfigured templates must remain disabled");
}

static void TestInfobipTemplateConfiguration()
{
    const string json = """
        {"Infobip":{"Templates":{
          "whatsapp_verification_code_v1:fr":{"Approved":true,"ProviderTemplateId":"fr-example"},
          "whatsapp_verification_code_v1:en_GB":{"Approved":true},
          "rent_due_today:fr":{"Approved":false},
          "rent_due_today:en":{"ProviderTemplateId":"unapproved-example"}
        }}}
        """;
    IConfigurationRoot Configuration(bool disableFrench = false)
    {
        var builder = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        if (disableFrench)
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Infobip:Templates:whatsapp_verification_code_v1:fr:Approved"] = "false"
            });
        return builder.Build();
    }
    InfobipOptions Bind(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddOptions<InfobipOptions>().Bind(configuration.GetSection("Infobip"));
        services.PostConfigure<InfobipOptions>(options => options.BindTemplateConfiguration(configuration));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<InfobipOptions>>().Value;
    }
    var configured = Bind(Configuration());
    Equal(4, configured.Templates.Count, "language-specific dictionary keys are reconstructed");
    var registry = new WhatsAppTemplateRegistry(Options.Create(configured));
    True(registry.TryGet("whatsapp_verification", "fr", out var french), "French definition");
    True(registry.IsApproved(french), "JSON French approval is effective");
    Equal("fr-example", french.ProviderTemplateId!, "provider id survives binding");
    True(registry.TryGet("whatsapp_verification", "en_GB", out var english) && registry.IsApproved(english), "English approval is independent");
    True(registry.TryGet("rent_due_today", "fr", out var disabled) && !registry.IsApproved(disabled), "false stays disabled");
    True(registry.TryGet("rent_due_today", "en", out var missingFlag) && !registry.IsApproved(missingFlag), "missing approval stays disabled");
    True(registry.TryGet("rent_overdue", "fr", out var absent) && !registry.IsApproved(absent), "missing entry stays disabled");
    var overridden = new WhatsAppTemplateRegistry(Options.Create(Bind(Configuration(disableFrench: true))));
    True(!overridden.IsApproved(french) && overridden.IsApproved(english), "higher-priority configuration can disable a single language");
    var empty = Bind(new ConfigurationBuilder().Build());
    Equal(0, empty.Templates.Count, "missing configuration remains inert");
}

static void TestWhatsAppInboundPolicy()
{
    foreach (var word in new[] { "STOP", " arrêt ", "DÉSABONNER", "unsubscribe" })
    {
        True(WhatsAppInboundPolicy.IsUnsubscribe(word), $"{word} should revoke transactional consent");
    }

    True(!WhatsAppInboundPolicy.IsUnsubscribe("Bonjour, j'ai une question"), "ordinary inbound text must be ignored");
    True(!WhatsAppInboundPolicy.IsUnsubscribe(null), "empty inbound text must be ignored");
}

static void TestInfobipInboundPayload()
{
    using var payload = JsonDocument.Parse("""
        {"results":[
          {"from":"237699000001","to":"237699000002","integrationType":"WHATSAPP",
           "messageId":"test-inbound-1","message":{"text":" STOP ","type":"TEXT"}},
          {"from":"237699000003","message":{"text":"Bonjour","type":"TEXT"}}
        ],"messageCount":2,"pendingMessageCount":0}
        """);
    var events = payload.RootElement.GetProperty("results");
    True(WhatsAppInboundPolicy.TryGetUnsubscribeSender(events[0], out var sender), "Infobip message.text STOP must be recognized");
    Equal("+237699000001", sender, "use normalized sender, not destination");
    True(!WhatsAppInboundPolicy.TryGetUnsubscribeSender(events[1], out _), "ordinary message must be ignored");
    True(!WhatsAppInboundPolicy.TryGetUnsubscribeSender(events[0].GetProperty("message"), out _), "nested text without its sender must not revoke consent");

    foreach (var json in new[]
             {
                 """{"from":"237699000001","message":{"text":"arrêt"}}""",
                 """{"from":"+237699000001","message":{"text":"STOP"}}""",
                 """{"from":"237699000001","content":{"text":"STOP"}}""",
                 """{"from":"237699000001","text":"STOP"}"""
             })
    {
        using var valid = JsonDocument.Parse(json);
        True(WhatsAppInboundPolicy.TryGetUnsubscribeSender(valid.RootElement, out _), "supported opt-out format must be recognized");
    }

    foreach (var json in new[]
             {
                 """{"message":{"text":"STOP"}}""",
                 """{"from":"invalid","message":{"text":"STOP"}}""",
                 """{"from":"237699000001","message":{"text":123}}""",
                 """{"from":"237699000001","message":null}""",
                 "null", "[]"
             })
    {
        using var invalid = JsonDocument.Parse(json);
        True(!WhatsAppInboundPolicy.TryGetUnsubscribeSender(invalid.RootElement, out _), "missing or malformed inbound data must be ignored");
    }
}

static void TestWhatsAppPersistenceModel()
{
    var options = new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=LontsiHomesModelCheck;Trusted_Connection=True;")
        .Options;
    using var context = new ApplicationDbContext(options);

    var userType = context.Model.FindEntityType(typeof(ApplicationUser))
        ?? throw new InvalidOperationException("ApplicationUser model missing");
    var uniqueWhatsApp = userType.GetIndexes().Single(index =>
        index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(ApplicationUser.NormalizedWhatsAppPhoneNumber) }));
    True(uniqueWhatsApp.IsUnique, "verified WhatsApp number index must be unique");
    True(uniqueWhatsApp.GetFilter()?.Contains(nameof(ApplicationUser.IsWhatsAppPhoneVerified), StringComparison.Ordinal) == true,
        "verified WhatsApp number index must be filtered to confirmed numbers");

    var deliveryType = context.Model.FindEntityType(typeof(NotificationDelivery))
        ?? throw new InvalidOperationException("NotificationDelivery model missing");
    var idempotencyIndex = deliveryType.GetIndexes().Single(index =>
        index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(NotificationDelivery.IdempotencyKey) }));
    True(idempotencyIndex.IsUnique, "outbox idempotency key must prevent duplicate sends");

    var consentType = context.Model.FindEntityType(typeof(UserCommunicationConsent))
        ?? throw new InvalidOperationException("UserCommunicationConsent model missing");
    foreach (var property in new[]
             {
                 nameof(UserCommunicationConsent.UserId), nameof(UserCommunicationConsent.Channel),
                 nameof(UserCommunicationConsent.Purpose), nameof(UserCommunicationConsent.Status),
                 nameof(UserCommunicationConsent.PhoneNumberE164), nameof(UserCommunicationConsent.TextVersion),
                 nameof(UserCommunicationConsent.Source), nameof(UserCommunicationConsent.GrantedAt),
                 nameof(UserCommunicationConsent.RevokedAt), nameof(UserCommunicationConsent.IpAddress),
                 nameof(UserCommunicationConsent.UserAgent)
             })
    {
        True(consentType.FindProperty(property) != null, $"consent audit field {property}");
    }
}

static void TestMainPhoneVerificationPause()
{
    var resolve = typeof(LontsiHomes.API.Controllers.AccountController).GetMethod("ResolveLandlordOnboardingStep",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    string Next(LandlordOnboardingStatusDto state, bool payments = false)
        => (string)resolve.Invoke(null, new object[] { state, payments })!;

    var status = new LandlordOnboardingStatusDto
    {
        Email = "landlord@example.test", EmailConfirmed = true,
        CountryCode = "+1", CountryIsoCode = "CA", Roles = new() { "Landlord" }
    };
    True(status.RequireMainPhoneVerification, "missing setting/status must default to requiring verification");
    Equal(LandlordOnboardingSteps.Phone, Next(status), "phone required by default");
    status.RequireMainPhoneVerification = false;
    Equal(LandlordOnboardingSteps.Kyc, Next(status), "paused phone step goes to identity submission");
    status.EmailConfirmed = false;
    Equal(LandlordOnboardingSteps.Email, Next(status), "email verification remains mandatory");
    status.EmailConfirmed = true;
    status.CountryCode = null;
    Equal(LandlordOnboardingSteps.Country, Next(status), "country remains mandatory");
    status.CountryCode = "+1";
    status.IsKycSubmitted = true;
    Equal(LandlordOnboardingSteps.Contract, Next(status), "terms remain mandatory");
    status.PlatformTermsAccepted = true;
    Equal(LandlordOnboardingSteps.Complete, Next(status), "onboarding completes without a verified main phone");
    True(!status.PhoneNumberConfirmed, "skipping must not mark phone verified");
    status.RequireMainPhoneVerification = true;
    Equal(LandlordOnboardingSteps.Phone, Next(status), "switch restores the requirement");

    status.RequireMainPhoneVerification = false;
    status.CountryCode = "+237";
    status.CountryIsoCode = "CM";
    Equal(LandlordOnboardingSteps.MobilePayments, Next(status, true), "payment details remain required");
    status.SubscriptionPaymentPhoneNumber = "+237690000001";
    status.SubscriptionPaymentChannel = PayoutChannelEnum.OrangeMoney;
    status.PayoutPhoneNumber = "+237670000001";
    status.PayoutChannel = PayoutChannelEnum.MtnMoney;
    Equal(LandlordOnboardingSteps.MobilePaymentVerification, Next(status, true), "payment OTPs remain required");

    var sanitize = typeof(LontsiHomes.API.Controllers.AccountController).GetMethod("SanitizeOnboardingStatusForAnonymous",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var sanitized = (LandlordOnboardingStatusDto)sanitize.Invoke(null, new object[] { status })!;
    True(!sanitized.RequireMainPhoneVerification, "portal receives the actual requirement");

    var adminStatus = new AdminUserVerificationStatusDto { Roles = new() { "Landlord" }, RequireMainPhoneVerification = false };
    var buildSteps = typeof(LontsiHomes.API.Controllers.AdminUsersController).GetMethod("BuildStepDtos",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var steps = (List<AdminUserOnboardingStepDto>)buildSteps.Invoke(null, new object[] { adminStatus })!;
    True(steps.All(step => step.Key != LandlordOnboardingSteps.Phone), "admin progress omits paused stage");
    adminStatus.RequireMainPhoneVerification = true;
    steps = (List<AdminUserOnboardingStepDto>)buildSteps.Invoke(null, new object[] { adminStatus })!;
    True(steps.Any(step => step.Key == LandlordOnboardingSteps.Phone), "admin progress restores stage");
}

static void TestWhatsAppUnchangedNumber()
{
    var user = new ApplicationUser
    {
        CountryCode = "+237", PhoneNumber = "600000001",
        WhatsAppPhoneNumber = "+237600000001", NormalizedWhatsAppPhoneNumber = "+237600000001",
        IsWhatsAppPhoneVerified = true, PendingWhatsAppPhoneNumber = "+237690000001"
    };
    foreach (var input in new[] { "+237600000001", "+237 600 00 00 01", "+237(600)00-00-01", user.PhoneNumber })
    {
        True(PhoneNumberHelper.TryNormalizeE164(user.CountryCode, input, out var normalized), "valid number normalizes");
        True(!WhatsAppNumberChange.RequiresVerification(user, normalized, true), "same active number rejected including formatting/main-phone variants");
    }
    Equal("+237690000001", user.PendingWhatsAppPhoneNumber!, "rejection preserves any different pending replacement");
    True(WhatsAppNumberChange.RequiresVerification(user, "+237690000001", true), "different number needs OTP, including resends");
    True(WhatsAppNumberChange.RequiresVerification(user, user.NormalizedWhatsAppPhoneNumber, false), "withdrawn consent can be reactivated on the same number");
    user.IsWhatsAppPhoneVerified = false;
    True(WhatsAppNumberChange.RequiresVerification(user, user.NormalizedWhatsAppPhoneNumber, true), "unverified number still needs OTP");
}

static void TestWhatsAppNumberChange()
{
    const string original = "+14165550101", replacement = "+14165550102";
    var verifiedAt = DateTimeOffset.UtcNow.AddDays(-1);
    var user = new ApplicationUser
    {
        Id = "number-change-test", CountryCode = "+1", PhoneNumber = original,
        UsePrimaryPhoneForWhatsApp = true, WhatsAppPhoneNumber = original,
        NormalizedWhatsAppPhoneNumber = original, IsWhatsAppPhoneVerified = true,
        WhatsAppPhoneVerifiedAt = verifiedAt
    };
    using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=LontsiHomesModelCheck;Trusted_Connection=True;").Options);
    context.Attach(user);
    var normalize = typeof(ApplicationDbContext).GetMethod("NormalizePhoneFields",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    WhatsAppNumberChange.Propose(user, replacement);
    normalize.Invoke(context, null); // Exercise the same-primary normalization hook, without connecting to a DB.
    Equal(original, user.WhatsAppPhoneNumber!, "old number survives proposal and save normalization");
    Equal(original, user.NormalizedWhatsAppPhoneNumber!, "old number keeps its unique reservation");
    Equal(replacement, user.PendingWhatsAppPhoneNumber!, "replacement remains pending");
    True(user.IsWhatsAppPhoneVerified && user.WhatsAppPhoneVerifiedAt == verifiedAt, "old verification preserved");
    WhatsAppNumberChange.Cancel(user);
    normalize.Invoke(context, null);
    True(user.PendingWhatsAppPhoneNumber == null, "cancel removes pending number");
    Equal(original, user.WhatsAppPhoneNumber!, "cancel preserves old destination");

    var otp = new CryptographicOtpService(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Otp:HashKey"] = "number-change-test-key" }).Build());
    var subject = WhatsAppNumberChange.OtpSubject(user.Id, replacement);
    var hash = otp.HashCode("123456", CommunicationPurposes.Transactional, subject);
    True(otp.VerifyCode("123456", hash, CommunicationPurposes.Transactional, subject), "new-number OTP accepted");
    True(!otp.VerifyCode("123456", hash, CommunicationPurposes.Transactional, WhatsAppNumberChange.OtpSubject(user.Id, original)), "code cannot approve another number");
    True(!otp.VerifyCode("654321", hash, CommunicationPurposes.Transactional, subject), "wrong OTP rejected");

    WhatsAppNumberChange.Propose(user, replacement);
    WhatsAppNumberChange.Confirm(user, false, DateTimeOffset.UtcNow);
    normalize.Invoke(context, null);
    Equal(replacement, user.WhatsAppPhoneNumber!, "verified replacement becomes current");
    Equal(replacement, user.NormalizedWhatsAppPhoneNumber!, "unique reservation moves to replacement");
    True(user.PendingWhatsAppPhoneNumber == null && user.IsWhatsAppPhoneVerified, "new number verified and pending cleared");
    True(!user.UsePrimaryPhoneForWhatsApp, "replacement is not overwritten by the old main phone");
}

static void TestOtpHashing()
{
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Otp:HashKey"] = "unit-test-secret" })
        .Build();
    var service = new CryptographicOtpService(configuration);
    var code = service.GenerateCode();
    Equal(6, code.Length, "OTP digit count");
    True(code.All(char.IsDigit), "OTP must contain digits only");
    var hash = service.HashCode(code, "Transactional", "user-1");
    True(!string.Equals(hash, code, StringComparison.Ordinal), "OTP must not be stored as plaintext");
    True(service.VerifyCode(code, hash, "Transactional", "user-1"), "correct OTP should verify");
    True(!service.VerifyCode(code, hash, "Transactional", "user-2"), "OTP hash must be bound to the user");
}

static async Task TestInfobipSmsClient()
{
    var handler = new RecordingHttpMessageHandler("{\"messages\":[{\"messageId\":\"sms-test-1\"}]}");
    var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.infobip.test/") };
    var options = Options.Create(new InfobipOptions
    {
        ApiKey = "test-api-key",
        SmsSender = "LontsiHomes"
    });
    var service = new InfobipSmsMessagingService(client, options, NullLogger<InfobipSmsMessagingService>.Instance);

    var result = await service.SendAsync("+237691478387", "Test OTP: 123456");
    True(result.Succeeded, "stubbed Infobip SMS request should succeed");
    Equal("sms-test-1", result.ProviderMessageId!, "SMS provider message id");
    Equal("https://api.infobip.test/sms/3/messages", handler.RequestUri!, "SMS endpoint");
    True(handler.Authorization == "App test-api-key", "SMS authorization header");
    True(handler.Body?.Contains("237691478387", StringComparison.Ordinal) == true, "SMS recipient payload");

    var inertHandler = new RecordingHttpMessageHandler("{}");
    var inert = new InfobipSmsMessagingService(
        new HttpClient(inertHandler) { BaseAddress = new Uri("https://api.infobip.test/") },
        Options.Create(new InfobipOptions()),
        NullLogger<InfobipSmsMessagingService>.Instance);
    var inertResult = await inert.SendAsync("+237691478387", "unused");
    True(!inertResult.Succeeded && inertHandler.CallCount == 0, "missing Infobip settings must prevent network calls");
}

static async Task TestInfobipWhatsAppClient()
{
    const string configurationJson = """
        {"Infobip":{"ApiKey":"test-api-key","WhatsAppSender":"237691478387",
        "Templates":{"whatsapp_verification_code_v1:fr":{"Approved":true}}}}
        """;
    var configuration = new ConfigurationBuilder()
        .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(configurationJson))).Build();
    var settings = configuration.GetSection("Infobip").Get<InfobipOptions>()!;
    settings.BindTemplateConfiguration(configuration);
    var options = Options.Create(settings);
    var registry = new WhatsAppTemplateRegistry(options);
    var handler = new RecordingHttpMessageHandler("{\"messages\":[{\"messageId\":\"wa-test-1\"}]}");
    var service = new InfobipWhatsAppMessagingService(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.infobip.test/") },
        options,
        registry,
        NullLogger<InfobipWhatsAppMessagingService>.Instance);

    var sent = await service.SendTemplateAsync(new WhatsAppTemplateMessage(
        "+237691478387",
        "whatsapp_verification_code_v1",
        "fr",
        new[] { "123456" },
        new[] { "123456" }));
    True(sent.Succeeded, "approved WhatsApp authentication template should be sent to the stub");
    Equal("https://api.infobip.test/whatsapp/1/message/template", handler.RequestUri!, "WhatsApp endpoint");
    True(handler.Body?.Contains("whatsapp_verification_code_v1", StringComparison.Ordinal) == true, "WhatsApp template payload");

    var rejected = await service.SendTemplateAsync(new WhatsAppTemplateMessage(
        "+237691478387",
        "rent_due_today",
        "fr",
        new[] { "A", "B", "C", "D" }));
    True(!rejected.Succeeded && handler.CallCount == 1, "unapproved template must fail before a network call");
}

static void TestScreenshotTemplateContracts()
{
    var registry = new WhatsAppTemplateRegistry(Options.Create(new InfobipOptions()));
    var contracts = new[]
    {
        ("rent_payment_receipt", "fr", 7, 0, true),
        ("landlord_rent_payment_received", "fr", 5, 1, false),
        ("rent_due_soon", "en", 6, 1, false),
        ("rent_due_today", "fr", 6, 1, false),
        ("rent_overdue", "fr", 6, 1, false),
        ("tenancy_ending_soon", "en", 4, 1, false),
        ("tenancy_request_received", "en", 4, 0, false),
        ("tenancy_request_status_update", "en", 5, 0, false),
        ("whatsapp_verification_code_v1", "fr", 1, 1, false)
    };
    foreach (var (template, language, body, buttons, document) in contracts)
    {
        True(registry.TryGetByTemplate(template, language, out var definition), template);
        Equal(body, definition.BodyPlaceholderCount, template + " body");
        Equal(buttons, definition.UrlButtonParameterCount, template + " buttons");
        Equal(document, definition.RequiresDocument, template + " document");
    }
}

static void TestWhatsAppDocumentOutbox()
{
    var body = new[] { "value1", "value2" };
    var header = new WhatsAppDocumentHeader("https://media.example.test/receipt.pdf?token=test", "receipt.pdf");
    var payload = WhatsAppOutboxPayload.Deserialize(WhatsAppOutboxPayload.Serialize(body, header));
    True(payload.BodyPlaceholders.SequenceEqual(body), "document body round trip");
    Equal(header, payload.Document!, "document header round trip");
    var legacyJson = WhatsAppOutboxPayload.Serialize(body, null);
    True(legacyJson.StartsWith('['), "text-only notifications retain legacy format");
    var legacy = WhatsAppOutboxPayload.Deserialize(legacyJson);
    True(legacy.BodyPlaceholders.SequenceEqual(body) && legacy.Document == null, "legacy queued notification");
    var rejected = false;
    try { WhatsAppOutboxPayload.Deserialize("{}"); }
    catch (JsonException) { rejected = true; }
    True(rejected, "malformed envelope is rejected");
}

static async Task TestWhatsAppDocumentClient()
{
    var settings = new InfobipOptions { ApiKey = "test-only", WhatsAppSender = "+237691478387" };
    settings.Templates["rent_payment_receipt:fr"] = new() { Approved = true };
    settings.Templates["whatsapp_verification_code_v1:fr"] = new() { Approved = true };
    settings.Templates["tenancy_request_received:en"] = new() { Approved = true };
    var options = Options.Create(settings);
    var registry = new WhatsAppTemplateRegistry(options);
    var handler = new RecordingHttpMessageHandler("{\"messages\":[{\"messageId\":\"document-test\"}]}");
    var service = new InfobipWhatsAppMessagingService(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.infobip.test/") },
        options, registry, NullLogger<InfobipWhatsAppMessagingService>.Instance);
    // Deliberately use numbered values: screenshots do not establish their business meaning.
    var message = new WhatsAppTemplateMessage("+237691478387", "rent_payment_receipt", "fr",
        Enumerable.Range(1, 7).Select(i => $"value{i}").ToArray(),
        Document: new WhatsAppDocumentHeader("https://media.example.test/receipt.pdf?token=test", "receipt.pdf"));
    True((await service.SendTemplateAsync(message)).Succeeded, "document sent to mock provider");
    using (var json = JsonDocument.Parse(handler.Body!))
    {
        var content = json.RootElement.GetProperty("messages")[0].GetProperty("content");
        Equal("fr", content.GetProperty("language").GetString()!, "receipt language");
        var data = content.GetProperty("templateData");
        True(!data.TryGetProperty("buttons", out _), "document template has no buttons property");
        Equal(7, data.GetProperty("body").GetProperty("placeholders").GetArrayLength(), "seven receipt placeholders");
        Equal("DOCUMENT", data.GetProperty("header").GetProperty("type").GetString()!, "document type");
        Equal(message.Document!.MediaUrl, data.GetProperty("header").GetProperty("mediaUrl").GetString()!, "media URL preserved");
        Equal("receipt.pdf", data.GetProperty("header").GetProperty("filename").GetString()!, "PDF filename");
    }
    var invalidMessages = new[]
    {
        message with { Document = null },
        message with { BodyPlaceholders = new[] { "1", "2", "3", "4", "5" } },
        message with { UrlButtonParameters = new[] { "unexpected" } },
        message with { Document = new("http://media.example.test/receipt.pdf", "receipt.pdf") },
        message with { Document = new("https://localhost:7059/receipt.pdf", "receipt.pdf") },
        message with { Document = new("file:///C:/receipt.pdf", "receipt.pdf") },
        message with { Document = new("https://media.example.test/receipt.pdf", "../receipt.pdf") },
        message with { Document = new("https://media.example.test/receipt.pdf", "receipt.html") },
        message with { TemplateName = "whatsapp_verification_code_v1", BodyPlaceholders = new[] { "123456" }, UrlButtonParameters = new[] { "123456" } }
    };
    foreach (var invalid in invalidMessages)
    {
        var result = await service.SendTemplateAsync(invalid);
        True(!result.Succeeded && result.ErrorCode == "template_parameters_invalid", "invalid payload rejected locally");
    }
    Equal(1, handler.CallCount, "invalid receipts never invoke provider");
    True((await service.SendTemplateAsync(new WhatsAppTemplateMessage("+237691478387",
        "tenancy_request_received", "en", new[] { "1", "2", "3", "4" }))).Succeeded, "body-only template");
    using var bodyOnlyJson = JsonDocument.Parse(handler.Body!);
    var bodyOnly = bodyOnlyJson.RootElement.GetProperty("messages")[0].GetProperty("content").GetProperty("templateData");
    True(!bodyOnly.TryGetProperty("buttons", out _) && !bodyOnly.TryGetProperty("header", out _), "absent components omitted");
}

static void TestWhatsAppVariableMappings()
{
    foreach (var language in new[] { PlatformLanguage.French, PlatformLanguage.English })
    {
        var receipt = new RentReceiptDto { TenantName = "Tenant", PropertyName = "Property", ApartmentName = "Unit",
            Amount = 150000.5m, Currency = "XAF", PaymentDate = Utc(2026, 9, 15), ReceiptNumber = "REC-123", TenantEmailLanguage = language };
        var values = WhatsAppTemplateValues.Receipt(receipt);
        True(values.SequenceEqual(new[] { "Tenant", "Property", "Unit",
            language == PlatformLanguage.French ? "150000,5" : "150000.5", "XAF",
            WhatsAppTemplateValues.Date(receipt.PaymentDate, language), "REC-123" }), "receipt order and language");
        var periods = new[] { new RentReminderPeriod { PeriodStartSnapshot = Utc(2026, 8, 1),
            PeriodEndSnapshot = Utc(2026, 8, 31), DueDateSnapshot = Utc(2026, 8, 5),
            IsTrigger = true, OutstandingAmountSnapshot = 30000 } };
        foreach (var category in new[] { RentReminderCategoryEnum.BeforeDue, RentReminderCategoryEnum.DueDate, RentReminderCategoryEnum.AfterDue })
        {
            var reminder = WhatsAppTemplateValues.Reminder("Tenant", "Property", "Unit", category, periods,
                Utc(2026, 9, 1), 30000, "XAF", language);
            Equal(6, reminder.Length, "six reminder values");
            Equal("Property", reminder[1], "separate property");
            Equal("Unit", reminder[2], "separate apartment");
            Equal(category == RentReminderCategoryEnum.AfterDue
                ? $"{WhatsAppTemplateValues.Date(periods[0].PeriodStartSnapshot, language)} – {WhatsAppTemplateValues.Date(periods[0].PeriodEndSnapshot, language)}"
                : WhatsAppTemplateValues.Date(periods[0].DueDateSnapshot, language), reminder[3], "period vs due date");
            Equal("30000", reminder[4], "amount without currency");
            Equal("XAF", reminder[5], "separate currency");
        }
    }
    Equal("renouvellement", WhatsAppTemplateValues.RequestType("renewal", PlatformLanguage.French), "French request type");
    Equal("fin de bail", WhatsAppTemplateValues.RequestType("termination", PlatformLanguage.French), "French termination");
    Equal("refusée", WhatsAppTemplateValues.RequestStatus("rejected", PlatformLanguage.French), "French decision");
    Equal("approved", WhatsAppTemplateValues.RequestStatus("approved", PlatformLanguage.English), "English decision");
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Infobip:UrlButtonParameters:rent_due_soon"] = "details/{tenancyId}" }).Build();
    Equal("details/123", WhatsAppTemplateValues.UrlButton(config, "rent_due_soon", "tenancyId", "123")![0], "configured suffix");
    True(WhatsAppTemplateValues.UrlButton(config, "rent_due_today", "tenancyId", "123") == null, "missing suffix not guessed");
}

static async Task TestReceiptMedia()
{
    var clock = new TestClock { Now = Utc(2026, 9, 15) };
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Infobip:ReceiptMediaBaseUrl"] = "https://media.example.test" }).Build();
    var provider = new EphemeralDataProtectionProvider();
    var media = new WhatsAppReceiptMedia(provider, configuration, clock);
    var reference = new WhatsAppReceiptReference(42, "receipt-secret", "REC-42");
    var header = media.Create(reference, "tenant-id", "fr")!;
    var token = Uri.UnescapeDataString(new Uri(header.MediaUrl).Query["?token=".Length..]);
    Equal(42, media.Validate(token)!.PaymentId, "payment bound to token");
    True(media.Validate(token + "tampered") == null, "tampered token denied");
    True(new WhatsAppReceiptMedia(new EphemeralDataProtectionProvider(), configuration, clock).Validate(token) == null, "wrong key denied");
    var unrelated = provider.CreateProtector("AnotherPurpose").Protect("{}");
    True(media.Validate(unrelated) == null, "wrong purpose denied");
    var payload = WhatsAppOutboxPayload.Deserialize(WhatsAppOutboxPayload.Serialize(new[] { "value" }, null, reference));
    Equal(reference, payload.ReceiptDocument!, "receipt reference survives queue persistence");
    using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().Options);
    var service = new TestReceiptService { Receipt = new RentReceiptDto { PaymentId = 42, TenantUserId = "tenant-id",
        VerificationCode = "receipt-secret", ReceiptNumber = "REC-42", Status = PaymentStatusEnum.Success,
        PaymentDate = clock.Now, IssuedAt = clock.Now, Currency = "XAF", TenantName = "Test Tenant" } };
    var controller = new LontsiHomes.API.Controllers.ReceiptsController(context, service, media)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
    True(await controller.WhatsAppPdf(null, default) is NotFoundResult && service.Reads == 0, "no token means no receipt lookup");
    var result = await controller.WhatsAppPdf(token, default) as FileContentResult;
    True(result != null && result.ContentType == "application/pdf" && Encoding.ASCII.GetString(result.FileContents, 0, 4) == "%PDF", "valid capability returns PDF not HTML");
    Equal("no-store, private", controller.Response.Headers["Cache-Control"].ToString(), "receipt not cacheable");
    service.Receipt.TenantUserId = "different-tenant";
    True(await controller.WhatsAppPdf(token, default) is NotFoundResult, "another tenant denied");
    service.Receipt.TenantUserId = "tenant-id";
    service.Receipt.Status = PaymentStatusEnum.Pending;
    True(await controller.WhatsAppPdf(token, default) is NotFoundResult, "unpaid receipt denied");
    service.Receipt.Status = PaymentStatusEnum.Success;
    service.Receipt.VerificationCode = "revoked";
    True(await controller.WhatsAppPdf(token, default) is NotFoundResult, "revoked receipt denied");
    service.Receipt = null;
    True(await controller.WhatsAppPdf(token, default) is NotFoundResult, "deleted receipt denied");
    clock.Now = clock.Now.AddHours(24);
    True(media.Validate(token) == null, "expired token denied at expiry boundary");
    foreach (var invalidBase in new[] { "", "https://localhost:64583", "http://media.example.test", "https://media.example.test?extra=true" })
    {
        configuration["Infobip:ReceiptMediaBaseUrl"] = invalidBase;
        True(media.Create(reference, "tenant-id", "fr") == null, "invalid public URL cannot generate media link");
    }
}

static void TestFlexibleDecimalAmounts()
{
    foreach (var sample in new[]
             {
                 "70000",
                 "70000.00",
                 "70000,00",
                 "70 000,00",
                 "70\u00a0000,00",
                 "70\u202f000,00",
                 "70,000.00",
                 "70.000,00"
             })
    {
        True(FlexibleDecimalParser.TryParse(sample, out var parsed), $"parse {sample}");
        Equal(70_000m, parsed, $"value {sample}");
    }

    True(FlexibleDecimalParser.TryParse("0,50", out var fractional), "parse French fraction");
    Equal(0.50m, fractional, "French fraction value");
    True(!FlexibleDecimalParser.TryParse("70 000 XAF", out _), "currency suffix rejected");
}

static bool IsValid(object value)
{
    var results = new List<ValidationResult>();
    return Validator.TryValidateObject(value, new ValidationContext(value), results, validateAllProperties: true);
}

static List<RentPeriodSeedDto> Fixed(DateTimeOffset start, DateTimeOffset end, int interval)
    => RentPeriodScheduleHelper.GeneratePeriods(
        start, end, TenancyEndBehaviorEnum.ExpireAutomatically, 30_000, start.Day,
        Utc(2024, 9, 1), interval, start);

static decimal Balance(IEnumerable<RentPeriodSeedDto> periods)
    => periods.Sum(period => Math.Max(0, period.Amount - period.PaidAmount));

static DateTimeOffset Utc(int year, int month, int day)
    => new(year, month, day, 0, 0, 0, TimeSpan.Zero);

static void Equal<T>(T expected, T actual, string message) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
}

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; }
    public override DateTimeOffset GetUtcNow() => Now;
}

sealed class TestReceiptService : IRentReceiptService
{
    public RentReceiptDto? Receipt { get; set; }
    public int Reads { get; private set; }
    public Task<RentReceiptDto?> GetReceiptAsync(int paymentId, CancellationToken cancellationToken = default)
    {
        Reads++;
        return Task.FromResult(Receipt);
    }
    public Task<RentReceiptDto?> EnsureReceiptAsync(int paymentId, string actorId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RentReceiptVerificationDto?> VerifyReceiptAsync(string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SendReceiptNotificationsAsync(RentReceiptDto receipt, bool tenant, bool landlord, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly string _responseJson;

    public RecordingHttpMessageHandler(string responseJson)
    {
        _responseJson = responseJson;
    }

    public int CallCount { get; private set; }
    public string? RequestUri { get; private set; }
    public string? Authorization { get; private set; }
    public string? Body { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        RequestUri = request.RequestUri?.ToString();
        Authorization = request.Headers.TryGetValues("Authorization", out var values) ? values.SingleOrDefault() : null;
        Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_responseJson, Encoding.UTF8, "application/json")
        };
    }
}
