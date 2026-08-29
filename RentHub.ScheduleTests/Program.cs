using Common.CommunicationModels;
using Common.Enums;
using Common.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentHub.API.Data;
using RentHub.API.Helpers;
using RentHub.API.Models.Entities;
using System.ComponentModel.DataAnnotations;

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
    ("Apartment floors accept only 0 through 30", TestApartmentFloorValidation),
    ("New managers can edit apartments by default", TestManagerDefaultPermission),
    ("Phone inputs remove all whitespace before validation", TestPhoneNumberNormalization),
    ("French and English decimal amounts parse identically", TestFlexibleDecimalAmounts),
    ("Production migration repairs partially-applied columns", TestProductionMigrationGuards),
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

static void TestMigrationModel()
{
    var options = new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=RentHubModelCheck;Trusted_Connection=True;")
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

static void TestProductionMigrationGuards()
{
    var options = new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=RentHubModelCheck;Trusted_Connection=True;")
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
    True(cleanupScript.Contains("DROP COLUMN [FutureRentPeriodCount]", StringComparison.Ordinal),
        "the obsolete rent horizon column is not removed");
}

static void TestApartmentFloorValidation()
{
    True(IsValid(new CreateApartmentRequest { PropertyId = 1, Name = "A", FloorNumber = 0 }), "ground floor should be valid");
    True(IsValid(new CreateApartmentRequest { PropertyId = 1, Name = "A", FloorNumber = 30 }), "floor 30 should be valid");
    True(!IsValid(new CreateApartmentRequest { PropertyId = 1, Name = "A", FloorNumber = -1 }), "negative floor should be rejected");
    True(!IsValid(new CreateApartmentRequest { PropertyId = 1, Name = "A", FloorNumber = 31 }), "floor 31 should be rejected");
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
