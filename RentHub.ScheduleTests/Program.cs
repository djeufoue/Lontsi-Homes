using Common.CommunicationModels;
using Common.Enums;
using Common.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentHub.API.Data;
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
    ("Up-to-date monthly tenancy starts with one requested future period", TestUpToDateMonthlyHorizon),
    ("Open tenancy completes the next payment group", TestOpenEndedHorizon),
    ("Quarterly balances are 90k, 60k, 30k and zero", TestQuarterlyBalances),
    ("Billing-group identity does not depend on payment status", TestStableGroupIdentity),
    ("One itemized PDF receipt covers multiple periods", TestItemizedReceiptPdf),
    ("Apartment floors accept only 0 through 30", TestApartmentFloorValidation),
    ("New managers can edit apartments by default", TestManagerDefaultPermission),
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
        Utc(2026, 8, 23), 2, 3, trackingStart);
    Equal(trackingStart.Date, periods[0].PeriodStart.Date, "first tracked period");
    True(periods.All(period => period.PeriodStart.Date >= trackingStart.Date), "covered history omitted");
}

static void TestOpenEndedHorizon()
{
    var periods = RentPeriodScheduleHelper.GeneratePeriods(
        Utc(2026, 8, 15), null, TenancyEndBehaviorEnum.NoEndDate, 30_000, 15,
        Utc(2026, 8, 23), 1, 6, Utc(2026, 8, 15));
    True(periods.Count >= 6, "horizon completes semiannual group");
    Equal(6, periods.Count(period => period.BillingGroupSequence == 0), "first semiannual group complete");
}

static void TestUpToDateMonthlyHorizon()
{
    var start = Utc(2024, 9, 15);
    var now = Utc(2026, 8, 23);
    var trackingStart = RentPeriodScheduleHelper.ResolveNextBillingGroupStart(start, now, 1);
    var periods = RentPeriodScheduleHelper.GeneratePeriods(
        start, null, TenancyEndBehaviorEnum.NoEndDate, 30_000, 15,
        now, 1, 1, trackingStart);
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

static bool IsValid(object value)
{
    var results = new List<ValidationResult>();
    return Validator.TryValidateObject(value, new ValidationContext(value), results, validateAllProperties: true);
}

static List<RentPeriodSeedDto> Fixed(DateTimeOffset start, DateTimeOffset end, int interval)
    => RentPeriodScheduleHelper.GeneratePeriods(
        start, end, TenancyEndBehaviorEnum.ExpireAutomatically, 30_000, start.Day,
        Utc(2024, 9, 1), 1, interval, start);

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
