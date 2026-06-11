using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Models.Entities;

namespace RentHub.API.Data
{
    /// <summary>
    /// Seeds the database with initial data such as subscription plans. This method is called
    /// when the application starts.
    /// </summary>
    public static class SeedData
    {
        public static void Initialize(IServiceProvider services, IConfiguration configuration)
        {
            using var scope = services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            context.Database.Migrate();

            // Safety net for existing databases that may have missed the snapshot migration.
            EnsureUserSubscriptionSnapshotColumns(context);
            EnsureApartmentReminderColumns(context);
            EnsureApartmentMemberRoleColumn(context);
            EnsureTenancyTenantColumnRemoved(context);
            EnsureTenancyApartmentShadowColumnRemoved(context);
            EnsureTenancyLifecycleSchema(context);
            EnsureUserVerificationColumns(context);
            EnsureSubscriptionCheckoutColumns(context);
            EnsureRentPaymentReceiptColumns(context);
            EnsureConversationTables(context);
            EnsureSystemTransferAccountsTable(context);
            EnsureOtpSendLogsTable(context);
            EnsurePlatformTermsColumns(context);
            EnsureStripeConnectColumns(context);
            EnsureLandlordKycTable(context);
            EnsureSubscriptionPlanCatalogColumns(context);

            SeedSubscriptionPlans(context);

            // Ensure roles exist (Admin, Landlord, Tenant, Owner, Manager, Visitor)
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            string[] roles = new[] { "Admin", "Landlord", "Tenant", "Owner", "Manager", "Visitor" };
            foreach (var roleName in roles)
            {
                if (!roleManager.Roles.Any(r => r.Name == roleName))
                {
                    roleManager.CreateAsync(new ApplicationRole { Name = roleName }).GetAwaiter().GetResult();
                }
            }

            SeedDefaultAdministrator(scope.ServiceProvider, configuration);

            // Additional seeding (roles, admin user) can be added here.
        }

        private static void SeedSubscriptionPlans(ApplicationDbContext context)
        {
            var now = DateTimeOffset.UtcNow;
            var planDefinitions = new[]
            {
                new SubscriptionPlan
                {
                    Name = "Starter",
                    Price = 5650M,
                    AnnualPrice = 56500M,
                    DurationInDays = 30,
                    Description = "Best for very small landlords",
                    AudienceLabel = "Best for very small landlords",
                    MaxProperties = 2,
                    MaxApartmentsPerProperty = 10,
                    MaxTotalApartments = 20,
                    FeatureHighlights = "Up to 2 properties|Up to 10 apartments / property|Up to 20 apartments total|Basic reminders and tenant tracking",
                    DisplayOrder = 10
                },
                new SubscriptionPlan
                {
                    Name = "Growth",
                    Price = 11300M,
                    AnnualPrice = 113000M,
                    DurationInDays = 30,
                    Description = "For growing landlords",
                    AudienceLabel = "For growing landlords",
                    MaxProperties = 5,
                    MaxApartmentsPerProperty = 25,
                    MaxTotalApartments = 100,
                    FeatureHighlights = "Up to 5 properties|Up to 25 apartments / property|Up to 100 apartments total|Tenant management and payment follow-up",
                    DisplayOrder = 20
                },
                new SubscriptionPlan
                {
                    Name = "Manager",
                    Price = 22600M,
                    AnnualPrice = 226000M,
                    DurationInDays = 30,
                    Description = "For active property managers",
                    AudienceLabel = "For active property managers",
                    MaxProperties = 15,
                    MaxApartmentsPerProperty = 50,
                    MaxTotalApartments = 400,
                    FeatureHighlights = "Up to 15 properties|Up to 50 apartments / property|Up to 400 apartments total|Reports, dashboard insights, and team access",
                    IsRecommended = true,
                    DisplayOrder = 30
                },
                new SubscriptionPlan
                {
                    Name = "Agency",
                    Price = 39550M,
                    AnnualPrice = 395500M,
                    DurationInDays = 30,
                    Description = "For agencies and operators",
                    AudienceLabel = "For agencies and operators",
                    MaxProperties = 40,
                    MaxApartmentsPerProperty = 150,
                    MaxTotalApartments = 1100,
                    FeatureHighlights = "Up to 40 properties|Up to 150 apartments / property|Up to 1,100 apartments total|Multi-user access and advanced automation",
                    DisplayOrder = 40
                },
                new SubscriptionPlan
                {
                    Name = "Portfolio",
                    Price = 56500M,
                    AnnualPrice = 565000M,
                    DurationInDays = 30,
                    Description = "For large portfolios",
                    AudienceLabel = "For large portfolios",
                    MaxProperties = 100,
                    MaxApartmentsPerProperty = 500,
                    MaxTotalApartments = 5100,
                    FeatureHighlights = "Up to 100 properties|Up to 500 apartments / property|Up to 5,100 apartments total|Priority support, export, and integrations",
                    DisplayOrder = 50
                },
                new SubscriptionPlan
                {
                    Name = "Enterprise Unlimited",
                    Price = 0M,
                    AnnualPrice = null,
                    DurationInDays = 30,
                    Description = "For enterprise portfolios",
                    AudienceLabel = "For enterprise portfolios",
                    MaxProperties = null,
                    MaxApartmentsPerProperty = null,
                    MaxTotalApartments = null,
                    FeatureHighlights = "Unlimited properties|Unlimited apartments|Dedicated onboarding|SLA support and custom workflows",
                    IsContactSales = true,
                    DisplayOrder = 60
                }
            };

            var existingPlans = context.SubscriptionPlans
                .IgnoreQueryFilters()
                .ToList();
            var currentNames = planDefinitions.Select(plan => plan.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var stalePlan in existingPlans.Where(plan => !currentNames.Contains(plan.Name)))
            {
                stalePlan.IsDeleted = true;
                stalePlan.DeletedAt ??= now;
                stalePlan.UpdatedAt = now;
            }

            foreach (var planDefinition in planDefinitions)
            {
                var plan = existingPlans.FirstOrDefault(existing =>
                    string.Equals(existing.Name, planDefinition.Name, StringComparison.OrdinalIgnoreCase));

                if (plan == null)
                {
                    planDefinition.CreatedAt = now;
                    context.SubscriptionPlans.Add(planDefinition);
                    continue;
                }

                plan.Name = planDefinition.Name;
                plan.Price = planDefinition.Price;
                plan.AnnualPrice = planDefinition.AnnualPrice;
                plan.DurationInDays = planDefinition.DurationInDays;
                plan.Description = planDefinition.Description;
                plan.AudienceLabel = planDefinition.AudienceLabel;
                plan.MaxProperties = planDefinition.MaxProperties;
                plan.MaxApartmentsPerProperty = planDefinition.MaxApartmentsPerProperty;
                plan.MaxTotalApartments = planDefinition.MaxTotalApartments;
                plan.FeatureHighlights = planDefinition.FeatureHighlights;
                plan.IsRecommended = planDefinition.IsRecommended;
                plan.IsContactSales = planDefinition.IsContactSales;
                plan.DisplayOrder = planDefinition.DisplayOrder;
                plan.IsDeleted = false;
                plan.DeletedAt = null;
                plan.DeletedBy = null;
                plan.UpdatedAt = now;
            }

            context.SaveChanges();
        }

        private static void EnsureSubscriptionPlanCatalogColumns(ApplicationDbContext context)
        {
            context.Database.ExecuteSqlRaw(@"
IF COL_LENGTH('SubscriptionPlans', 'AnnualPrice') IS NULL
BEGIN
    ALTER TABLE [SubscriptionPlans] ADD [AnnualPrice] decimal(18,2) NULL;
END

IF COL_LENGTH('SubscriptionPlans', 'MaxTotalApartments') IS NULL
BEGIN
    ALTER TABLE [SubscriptionPlans] ADD [MaxTotalApartments] int NULL;
END

IF COL_LENGTH('SubscriptionPlans', 'AudienceLabel') IS NULL
BEGIN
    ALTER TABLE [SubscriptionPlans] ADD [AudienceLabel] nvarchar(max) NULL;
END

IF COL_LENGTH('SubscriptionPlans', 'FeatureHighlights') IS NULL
BEGIN
    ALTER TABLE [SubscriptionPlans] ADD [FeatureHighlights] nvarchar(max) NULL;
END

IF COL_LENGTH('SubscriptionPlans', 'IsRecommended') IS NULL
BEGIN
    ALTER TABLE [SubscriptionPlans]
    ADD [IsRecommended] bit NOT NULL
        CONSTRAINT [DF_SubscriptionPlans_IsRecommended] DEFAULT(0);
END

IF COL_LENGTH('SubscriptionPlans', 'IsContactSales') IS NULL
BEGIN
    ALTER TABLE [SubscriptionPlans]
    ADD [IsContactSales] bit NOT NULL
        CONSTRAINT [DF_SubscriptionPlans_IsContactSales] DEFAULT(0);
END

IF COL_LENGTH('SubscriptionPlans', 'DisplayOrder') IS NULL
BEGIN
    ALTER TABLE [SubscriptionPlans]
    ADD [DisplayOrder] int NOT NULL
        CONSTRAINT [DF_SubscriptionPlans_DisplayOrder] DEFAULT(0);
END
");
        }

        private static void EnsureOtpSendLogsTable(ApplicationDbContext context)
        {
            context.Database.ExecuteSqlRaw(@"
IF OBJECT_ID(N'[OtpSendLogs]', N'U') IS NULL
BEGIN
    CREATE TABLE [OtpSendLogs]
    (
        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_OtpSendLogs] PRIMARY KEY,
        [UserId] nvarchar(450) NOT NULL,
        [Purpose] nvarchar(64) NOT NULL,
        [Channel] nvarchar(20) NOT NULL,
        [Recipient] nvarchar(64) NOT NULL,
        [SentAt] datetimeoffset NOT NULL
            CONSTRAINT [DF_OtpSendLogs_SentAt] DEFAULT(SYSDATETIMEOFFSET()),
        CONSTRAINT [FK_OtpSendLogs_AspNetUsers_UserId]
            FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers]([Id]) ON DELETE CASCADE
    );
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_OtpSendLogs_UserId_Purpose_SentAt'
      AND [object_id] = OBJECT_ID(N'[OtpSendLogs]')
)
BEGIN
    CREATE INDEX [IX_OtpSendLogs_UserId_Purpose_SentAt]
    ON [OtpSendLogs]([UserId], [Purpose], [SentAt]);
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_OtpSendLogs_UserId_Purpose_Recipient_SentAt'
      AND [object_id] = OBJECT_ID(N'[OtpSendLogs]')
)
BEGIN
    CREATE INDEX [IX_OtpSendLogs_UserId_Purpose_Recipient_SentAt]
    ON [OtpSendLogs]([UserId], [Purpose], [Recipient], [SentAt]);
END
");
        }

        private static void EnsurePlatformTermsColumns(ApplicationDbContext context)
        {
            context.Database.ExecuteSqlRaw(@"
IF COL_LENGTH('AspNetUsers', 'PlatformTermsAccepted') IS NULL
BEGIN
    ALTER TABLE [AspNetUsers]
    ADD [PlatformTermsAccepted] bit NOT NULL
        CONSTRAINT [DF_AspNetUsers_PlatformTermsAccepted] DEFAULT(0);
END

IF COL_LENGTH('AspNetUsers', 'PlatformTermsAcceptedAt') IS NULL
BEGIN
    ALTER TABLE [AspNetUsers]
    ADD [PlatformTermsAcceptedAt] datetimeoffset NULL;
END

IF COL_LENGTH('AspNetUsers', 'PlatformTermsSignatureName') IS NULL
BEGIN
    ALTER TABLE [AspNetUsers]
    ADD [PlatformTermsSignatureName] nvarchar(160) NULL;
END

IF COL_LENGTH('AspNetUsers', 'PlatformTermsVersion') IS NULL
BEGIN
    ALTER TABLE [AspNetUsers]
    ADD [PlatformTermsVersion] nvarchar(40) NULL;
END
");
        }

        private static void EnsureLandlordKycTable(ApplicationDbContext context)
        {
            context.Database.ExecuteSqlRaw(@"
IF OBJECT_ID(N'[LandlordKycProfiles]', N'U') IS NULL
BEGIN
    CREATE TABLE [LandlordKycProfiles]
    (
        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_LandlordKycProfiles] PRIMARY KEY,
        [UserId] nvarchar(450) NOT NULL,
        [DocumentType] int NOT NULL,
        [Status] int NOT NULL
            CONSTRAINT [DF_LandlordKycProfiles_Status] DEFAULT(1),
        [FaceFrontPath] nvarchar(1024) NOT NULL,
        [FaceFrontContentType] nvarchar(max) NOT NULL
            CONSTRAINT [DF_LandlordKycProfiles_FaceFrontContentType] DEFAULT(''),
        [FaceFrontOriginalFileName] nvarchar(max) NOT NULL
            CONSTRAINT [DF_LandlordKycProfiles_FaceFrontOriginalFileName] DEFAULT(''),
        [FaceRightPath] nvarchar(1024) NOT NULL,
        [FaceRightContentType] nvarchar(max) NOT NULL
            CONSTRAINT [DF_LandlordKycProfiles_FaceRightContentType] DEFAULT(''),
        [FaceRightOriginalFileName] nvarchar(max) NOT NULL
            CONSTRAINT [DF_LandlordKycProfiles_FaceRightOriginalFileName] DEFAULT(''),
        [FaceLeftPath] nvarchar(1024) NOT NULL,
        [FaceLeftContentType] nvarchar(max) NOT NULL
            CONSTRAINT [DF_LandlordKycProfiles_FaceLeftContentType] DEFAULT(''),
        [FaceLeftOriginalFileName] nvarchar(max) NOT NULL
            CONSTRAINT [DF_LandlordKycProfiles_FaceLeftOriginalFileName] DEFAULT(''),
        [DocumentFrontPath] nvarchar(1024) NOT NULL,
        [DocumentFrontContentType] nvarchar(max) NOT NULL
            CONSTRAINT [DF_LandlordKycProfiles_DocumentFrontContentType] DEFAULT(''),
        [DocumentFrontOriginalFileName] nvarchar(max) NOT NULL
            CONSTRAINT [DF_LandlordKycProfiles_DocumentFrontOriginalFileName] DEFAULT(''),
        [DocumentBackPath] nvarchar(1024) NULL,
        [DocumentBackContentType] nvarchar(max) NULL,
        [DocumentBackOriginalFileName] nvarchar(max) NULL,
        [RejectFaceFront] bit NOT NULL
            CONSTRAINT [DF_LandlordKycProfiles_RejectFaceFront] DEFAULT(0),
        [RejectFaceRight] bit NOT NULL
            CONSTRAINT [DF_LandlordKycProfiles_RejectFaceRight] DEFAULT(0),
        [RejectFaceLeft] bit NOT NULL
            CONSTRAINT [DF_LandlordKycProfiles_RejectFaceLeft] DEFAULT(0),
        [RejectDocumentFront] bit NOT NULL
            CONSTRAINT [DF_LandlordKycProfiles_RejectDocumentFront] DEFAULT(0),
        [RejectDocumentBack] bit NOT NULL
            CONSTRAINT [DF_LandlordKycProfiles_RejectDocumentBack] DEFAULT(0),
        [SubmittedAt] datetimeoffset NOT NULL
            CONSTRAINT [DF_LandlordKycProfiles_SubmittedAt] DEFAULT(SYSDATETIMEOFFSET()),
        [ReviewedById] nvarchar(450) NULL,
        [ReviewedAt] datetimeoffset NULL,
        [ReviewNote] nvarchar(max) NULL,
        [CreatedAt] datetimeoffset NOT NULL
            CONSTRAINT [DF_LandlordKycProfiles_CreatedAt] DEFAULT(SYSDATETIMEOFFSET()),
        [UpdatedAt] datetimeoffset NULL,
        CONSTRAINT [FK_LandlordKycProfiles_AspNetUsers_UserId]
            FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers]([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_LandlordKycProfiles_AspNetUsers_ReviewedById]
            FOREIGN KEY ([ReviewedById]) REFERENCES [AspNetUsers]([Id])
    );
END

IF COL_LENGTH(N'[LandlordKycProfiles]', N'RejectFaceFront') IS NULL
BEGIN
    ALTER TABLE [LandlordKycProfiles]
    ADD [RejectFaceFront] bit NOT NULL
        CONSTRAINT [DF_LandlordKycProfiles_RejectFaceFront] DEFAULT(0) WITH VALUES;
END

IF COL_LENGTH(N'[LandlordKycProfiles]', N'RejectFaceRight') IS NULL
BEGIN
    ALTER TABLE [LandlordKycProfiles]
    ADD [RejectFaceRight] bit NOT NULL
        CONSTRAINT [DF_LandlordKycProfiles_RejectFaceRight] DEFAULT(0) WITH VALUES;
END

IF COL_LENGTH(N'[LandlordKycProfiles]', N'RejectFaceLeft') IS NULL
BEGIN
    ALTER TABLE [LandlordKycProfiles]
    ADD [RejectFaceLeft] bit NOT NULL
        CONSTRAINT [DF_LandlordKycProfiles_RejectFaceLeft] DEFAULT(0) WITH VALUES;
END

IF COL_LENGTH(N'[LandlordKycProfiles]', N'RejectDocumentFront') IS NULL
BEGIN
    ALTER TABLE [LandlordKycProfiles]
    ADD [RejectDocumentFront] bit NOT NULL
        CONSTRAINT [DF_LandlordKycProfiles_RejectDocumentFront] DEFAULT(0) WITH VALUES;
END

IF COL_LENGTH(N'[LandlordKycProfiles]', N'RejectDocumentBack') IS NULL
BEGIN
    ALTER TABLE [LandlordKycProfiles]
    ADD [RejectDocumentBack] bit NOT NULL
        CONSTRAINT [DF_LandlordKycProfiles_RejectDocumentBack] DEFAULT(0) WITH VALUES;
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_LandlordKycProfiles_UserId'
      AND [object_id] = OBJECT_ID(N'[LandlordKycProfiles]')
)
BEGIN
    CREATE UNIQUE INDEX [IX_LandlordKycProfiles_UserId]
    ON [LandlordKycProfiles]([UserId]);
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_LandlordKycProfiles_Status_SubmittedAt'
      AND [object_id] = OBJECT_ID(N'[LandlordKycProfiles]')
)
BEGIN
    CREATE INDEX [IX_LandlordKycProfiles_Status_SubmittedAt]
    ON [LandlordKycProfiles]([Status], [SubmittedAt]);
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_LandlordKycProfiles_ReviewedById'
      AND [object_id] = OBJECT_ID(N'[LandlordKycProfiles]')
)
BEGIN
    CREATE INDEX [IX_LandlordKycProfiles_ReviewedById]
    ON [LandlordKycProfiles]([ReviewedById]);
END
");
        }

        private static void EnsureStripeConnectColumns(ApplicationDbContext context)
        {
            context.Database.ExecuteSqlRaw(@"
IF COL_LENGTH('AspNetUsers', 'CountryIsoCode') IS NULL
BEGIN
    ALTER TABLE [AspNetUsers] ADD [CountryIsoCode] nvarchar(2) NULL;
END

IF COL_LENGTH('AspNetUsers', 'StripeConnectAccountId') IS NULL
BEGIN
    ALTER TABLE [AspNetUsers] ADD [StripeConnectAccountId] nvarchar(128) NULL;
END

IF COL_LENGTH('AspNetUsers', 'StripePayoutDetailsSubmitted') IS NULL
BEGIN
    ALTER TABLE [AspNetUsers]
    ADD [StripePayoutDetailsSubmitted] bit NOT NULL
        CONSTRAINT [DF_AspNetUsers_StripePayoutDetailsSubmitted] DEFAULT(0) WITH VALUES;
END

IF COL_LENGTH('AspNetUsers', 'StripeChargesEnabled') IS NULL
BEGIN
    ALTER TABLE [AspNetUsers]
    ADD [StripeChargesEnabled] bit NOT NULL
        CONSTRAINT [DF_AspNetUsers_StripeChargesEnabled] DEFAULT(0) WITH VALUES;
END

IF COL_LENGTH('AspNetUsers', 'StripePayoutsEnabled') IS NULL
BEGIN
    ALTER TABLE [AspNetUsers]
    ADD [StripePayoutsEnabled] bit NOT NULL
        CONSTRAINT [DF_AspNetUsers_StripePayoutsEnabled] DEFAULT(0) WITH VALUES;
END

IF COL_LENGTH('AspNetUsers', 'StripePayoutRequirementsSummary') IS NULL
BEGIN
    ALTER TABLE [AspNetUsers] ADD [StripePayoutRequirementsSummary] nvarchar(1024) NULL;
END

IF COL_LENGTH('AspNetUsers', 'StripePayoutDisabledReason') IS NULL
BEGIN
    ALTER TABLE [AspNetUsers] ADD [StripePayoutDisabledReason] nvarchar(512) NULL;
END

IF COL_LENGTH('AspNetUsers', 'StripePayoutSetupStartedAt') IS NULL
BEGIN
    ALTER TABLE [AspNetUsers] ADD [StripePayoutSetupStartedAt] datetimeoffset NULL;
END

IF COL_LENGTH('AspNetUsers', 'StripePayoutSetupCompletedAt') IS NULL
BEGIN
    ALTER TABLE [AspNetUsers] ADD [StripePayoutSetupCompletedAt] datetimeoffset NULL;
END

IF COL_LENGTH('AspNetUsers', 'StripePayoutStatusUpdatedAt') IS NULL
BEGIN
    ALTER TABLE [AspNetUsers] ADD [StripePayoutStatusUpdatedAt] datetimeoffset NULL;
END
");
        }

        private static void SeedDefaultAdministrator(IServiceProvider serviceProvider, IConfiguration configuration)
        {
            var adminEmail = configuration["AdminSeed:Email"];
            var adminPassword = configuration["AdminSeed:Password"];
            var adminFullName = configuration["AdminSeed:FullName"] ?? "System Administrator";
            var adminCountryCode = configuration["AdminSeed:CountryCode"] ?? "+237";

            if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
            {
                return;
            }

            var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var admin = userManager.FindByEmailAsync(adminEmail).GetAwaiter().GetResult();
            if (admin == null)
            {
                admin = new ApplicationUser
                {
                    UserName = adminEmail,
                    Email = adminEmail,
                    EmailConfirmed = true,
                    FullName = adminFullName,
                    CountryCode = adminCountryCode
                };

                var createResult = userManager.CreateAsync(admin, adminPassword).GetAwaiter().GetResult();
                if (!createResult.Succeeded)
                {
                    return;
                }

                userManager.AddToRoleAsync(admin, "Admin").GetAwaiter().GetResult();
            }
        }

        private static void EnsureUserSubscriptionSnapshotColumns(ApplicationDbContext context)
        {
            context.Database.ExecuteSqlRaw(@"
                IF COL_LENGTH('UserSubscriptions', 'PlanDurationInDaysSnapshot') IS NULL
                BEGIN
                    ALTER TABLE [UserSubscriptions]
                    ADD [PlanDurationInDaysSnapshot] int NOT NULL
                        CONSTRAINT [DF_UserSubscriptions_PlanDurationInDaysSnapshot] DEFAULT(0);
                END

                IF COL_LENGTH('UserSubscriptions', 'PlanMaxApartmentsPerPropertySnapshot') IS NULL
                BEGIN
                    ALTER TABLE [UserSubscriptions]
                    ADD [PlanMaxApartmentsPerPropertySnapshot] int NULL;
                END

                IF COL_LENGTH('UserSubscriptions', 'PlanMaxPropertiesSnapshot') IS NULL
                BEGIN
                    ALTER TABLE [UserSubscriptions]
                    ADD [PlanMaxPropertiesSnapshot] int NULL;
                END

                IF COL_LENGTH('UserSubscriptions', 'PlanNameSnapshot') IS NULL
                BEGIN
                    ALTER TABLE [UserSubscriptions]
                    ADD [PlanNameSnapshot] nvarchar(max) NOT NULL
                        CONSTRAINT [DF_UserSubscriptions_PlanNameSnapshot] DEFAULT('');
                END

                IF COL_LENGTH('UserSubscriptions', 'PlanPriceSnapshot') IS NULL
                BEGIN
                    ALTER TABLE [UserSubscriptions]
                    ADD [PlanPriceSnapshot] decimal(18,2) NOT NULL
                        CONSTRAINT [DF_UserSubscriptions_PlanPriceSnapshot] DEFAULT(0);
                END
                ");

            context.Database.ExecuteSqlRaw(@"
                UPDATE us
                SET
                    us.PlanNameSnapshot = CASE
                        WHEN us.PlanNameSnapshot IS NULL OR us.PlanNameSnapshot = '' THEN ISNULL(sp.Name, '')
                        ELSE us.PlanNameSnapshot
                    END,
                    us.PlanPriceSnapshot = CASE
                        WHEN us.PlanPriceSnapshot = 0 THEN ISNULL(sp.Price, 0)
                        ELSE us.PlanPriceSnapshot
                    END,
                    us.PlanDurationInDaysSnapshot = CASE
                        WHEN us.PlanDurationInDaysSnapshot = 0 THEN ISNULL(sp.DurationInDays, 0)
                        ELSE us.PlanDurationInDaysSnapshot
                    END,
                    us.PlanMaxPropertiesSnapshot = COALESCE(us.PlanMaxPropertiesSnapshot, sp.MaxProperties),
                    us.PlanMaxApartmentsPerPropertySnapshot = COALESCE(us.PlanMaxApartmentsPerPropertySnapshot, sp.MaxApartmentsPerProperty)
                FROM UserSubscriptions us
                LEFT JOIN SubscriptionPlans sp ON sp.Id = us.SubscriptionPlanId;
            ");
        }

        private static void EnsureApartmentReminderColumns(ApplicationDbContext context)
        {
            context.Database.ExecuteSqlRaw(@"
                IF COL_LENGTH('Apartments', 'RentReminderDaysBeforeDue') IS NULL
                BEGIN
                    ALTER TABLE [Apartments]
                    ADD [RentReminderDaysBeforeDue] int NOT NULL
                        CONSTRAINT [DF_Apartments_RentReminderDaysBeforeDue] DEFAULT(10);
                END

                IF COL_LENGTH('Apartments', 'LeaseTerminationReminderDaysBeforeEnd') IS NULL
                BEGIN
                    ALTER TABLE [Apartments]
                    ADD [LeaseTerminationReminderDaysBeforeEnd] int NOT NULL
                        CONSTRAINT [DF_Apartments_LeaseTerminationReminderDaysBeforeEnd] DEFAULT(30);
                END
            ");

            context.Database.ExecuteSqlRaw(@"
                UPDATE [Apartments]
                SET
                    [RentReminderDaysBeforeDue] = CASE
                        WHEN [RentReminderDaysBeforeDue] <= 0 THEN 10
                        ELSE [RentReminderDaysBeforeDue]
                    END,
                    [LeaseTerminationReminderDaysBeforeEnd] = CASE
                        WHEN [LeaseTerminationReminderDaysBeforeEnd] <= 0 THEN 30
                        ELSE [LeaseTerminationReminderDaysBeforeEnd]
                    END
            ");
        }

        private static void EnsureApartmentMemberRoleColumn(ApplicationDbContext context)
        {
            context.Database.ExecuteSqlRaw(@"
                IF COL_LENGTH('ApartmentOwners', 'Role') IS NULL
                BEGIN
                    ALTER TABLE [ApartmentOwners]
                    ADD [Role] int NOT NULL
                        CONSTRAINT [DF_ApartmentOwners_Role] DEFAULT(1);
                END
            ");

            context.Database.ExecuteSqlRaw(@"
                UPDATE [ApartmentOwners]
                SET [Role] = CASE
                    WHEN [Role] IS NULL OR [Role] <= 0 THEN 1
                    ELSE [Role]
                END
            ");
        }

        private static void EnsureTenancyTenantColumnRemoved(ApplicationDbContext context)
        {
            context.Database.ExecuteSqlRaw(@"
                IF EXISTS (
                    SELECT 1
                    FROM sys.foreign_keys
                    WHERE name = 'FK_Tenancies_AspNetUsers_TenantId'
                )
                BEGIN
                    ALTER TABLE [Tenancies] DROP CONSTRAINT [FK_Tenancies_AspNetUsers_TenantId];
                END

                IF EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = 'IX_Tenancies_TenantId'
                      AND object_id = OBJECT_ID('[Tenancies]')
                )
                BEGIN
                    DROP INDEX [IX_Tenancies_TenantId] ON [Tenancies];
                END

                IF COL_LENGTH('Tenancies', 'TenantId') IS NOT NULL
                BEGIN
                    ALTER TABLE [Tenancies] DROP COLUMN [TenantId];
                END
            ");
        }

        private static void EnsureTenancyApartmentShadowColumnRemoved(ApplicationDbContext context)
        {
            context.Database.ExecuteSqlRaw(@"
                IF EXISTS (
                    SELECT 1
                    FROM sys.foreign_keys
                    WHERE name = 'FK_Tenancies_Apartments_ApartmentId1'
                )
                BEGIN
                    ALTER TABLE [Tenancies] DROP CONSTRAINT [FK_Tenancies_Apartments_ApartmentId1];
                END

                IF EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = 'IX_Tenancies_ApartmentId1'
                      AND object_id = OBJECT_ID('[Tenancies]')
                )
                BEGIN
                    DROP INDEX [IX_Tenancies_ApartmentId1] ON [Tenancies];
                END

                IF COL_LENGTH('Tenancies', 'ApartmentId1') IS NOT NULL
                BEGIN
                    ALTER TABLE [Tenancies] DROP COLUMN [ApartmentId1];
                END
            ");
        }

        private static void EnsureTenancyLifecycleSchema(ApplicationDbContext context)
        {
            context.Database.ExecuteSqlRaw(@"
IF COL_LENGTH('Tenancies', 'RentDueDay') IS NULL
BEGIN
    ALTER TABLE [Tenancies]
    ADD [RentDueDay] int NOT NULL
        CONSTRAINT [DF_Tenancies_RentDueDay] DEFAULT(1) WITH VALUES;
END

IF COL_LENGTH('Tenancies', 'EndBehavior') IS NULL
BEGIN
    ALTER TABLE [Tenancies]
    ADD [EndBehavior] int NOT NULL
        CONSTRAINT [DF_Tenancies_EndBehavior] DEFAULT(3) WITH VALUES;
END

IF COL_LENGTH('Tenancies', 'TerminatedAt') IS NULL
BEGIN
    ALTER TABLE [Tenancies] ADD [TerminatedAt] datetimeoffset NULL;
END

IF COL_LENGTH('Tenancies', 'TerminationReason') IS NULL
BEGIN
    ALTER TABLE [Tenancies] ADD [TerminationReason] int NULL;
END

IF COL_LENGTH('Tenancies', 'TerminationNotes') IS NULL
BEGIN
    ALTER TABLE [Tenancies] ADD [TerminationNotes] nvarchar(512) NULL;
END

IF COL_LENGTH('Tenancies', 'TerminatedBy') IS NULL
BEGIN
    ALTER TABLE [Tenancies] ADD [TerminatedBy] nvarchar(max) NULL;
END

IF OBJECT_ID(N'[RentPeriods]', N'U') IS NULL
BEGIN
    CREATE TABLE [RentPeriods](
        [Id] int NOT NULL IDENTITY,
        [TenancyId] int NOT NULL,
        [PeriodStart] datetimeoffset NOT NULL,
        [PeriodEnd] datetimeoffset NOT NULL,
        [DueDate] datetimeoffset NOT NULL,
        [Amount] decimal(14,2) NOT NULL,
        [PaidAmount] decimal(14,2) NOT NULL CONSTRAINT [DF_RentPeriods_PaidAmount] DEFAULT(0),
        [PaidDate] datetimeoffset NULL,
        [PaymentId] int NULL,
        [PaymentReference] nvarchar(128) NOT NULL CONSTRAINT [DF_RentPeriods_PaymentReference] DEFAULT(N''),
        [Status] int NOT NULL CONSTRAINT [DF_RentPeriods_Status] DEFAULT(0),
        [IsDeleted] bit NOT NULL CONSTRAINT [DF_RentPeriods_IsDeleted] DEFAULT(0),
        [CreatedBy] nvarchar(max) NULL,
        [CreatedAt] datetimeoffset NOT NULL CONSTRAINT [DF_RentPeriods_CreatedAt] DEFAULT(SYSDATETIMEOFFSET()),
        [UpdatedBy] nvarchar(max) NULL,
        [UpdatedAt] datetimeoffset NULL,
        [DeletedBy] nvarchar(max) NULL,
        [DeletedAt] datetimeoffset NULL,
        CONSTRAINT [PK_RentPeriods] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RentPeriods_Tenancies_TenancyId] FOREIGN KEY ([TenancyId]) REFERENCES [Tenancies]([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_RentPeriods_Payments_PaymentId] FOREIGN KEY ([PaymentId]) REFERENCES [Payments]([Id])
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_RentPeriods_TenancyId_PeriodStart' AND [object_id] = OBJECT_ID(N'[RentPeriods]'))
BEGIN
    CREATE UNIQUE INDEX [IX_RentPeriods_TenancyId_PeriodStart]
    ON [RentPeriods]([TenancyId], [PeriodStart])
    WHERE [IsDeleted] = 0;
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_RentPeriods_TenancyId_Status_DueDate' AND [object_id] = OBJECT_ID(N'[RentPeriods]'))
BEGIN
    CREATE INDEX [IX_RentPeriods_TenancyId_Status_DueDate]
    ON [RentPeriods]([TenancyId], [Status], [DueDate]);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_RentPeriods_PaymentId' AND [object_id] = OBJECT_ID(N'[RentPeriods]'))
BEGIN
    CREATE INDEX [IX_RentPeriods_PaymentId]
    ON [RentPeriods]([PaymentId]);
END
");
        }

        private static void EnsureUserVerificationColumns(ApplicationDbContext context)
        {
            context.Database.ExecuteSqlRaw(@"
                IF COL_LENGTH('AspNetUsers', 'UsePrimaryPhoneForSubscriptionPayments') IS NULL
                BEGIN
                    ALTER TABLE [AspNetUsers]
                    ADD [UsePrimaryPhoneForSubscriptionPayments] bit NOT NULL
                        CONSTRAINT [DF_AspNetUsers_UsePrimaryPhoneForSubscriptionPayments] DEFAULT(0);
                END

                IF COL_LENGTH('AspNetUsers', 'SubscriptionPaymentPhoneNumber') IS NULL
                BEGIN
                    ALTER TABLE [AspNetUsers]
                    ADD [SubscriptionPaymentPhoneNumber] nvarchar(max) NULL;
                END

                IF COL_LENGTH('AspNetUsers', 'SubscriptionPaymentChannel') IS NULL
                BEGIN
                    ALTER TABLE [AspNetUsers]
                    ADD [SubscriptionPaymentChannel] int NULL;
                END

                IF COL_LENGTH('AspNetUsers', 'IsSubscriptionPaymentPhoneVerified') IS NULL
                BEGIN
                    ALTER TABLE [AspNetUsers]
                    ADD [IsSubscriptionPaymentPhoneVerified] bit NOT NULL
                        CONSTRAINT [DF_AspNetUsers_IsSubscriptionPaymentPhoneVerified] DEFAULT(0);
                END

                IF COL_LENGTH('AspNetUsers', 'SubscriptionPaymentPhoneVerifiedAt') IS NULL
                BEGIN
                    ALTER TABLE [AspNetUsers]
                    ADD [SubscriptionPaymentPhoneVerifiedAt] datetimeoffset NULL;
                END

                IF COL_LENGTH('AspNetUsers', 'UsePrimaryPhoneForRentPayouts') IS NULL
                BEGIN
                    ALTER TABLE [AspNetUsers]
                    ADD [UsePrimaryPhoneForRentPayouts] bit NOT NULL
                        CONSTRAINT [DF_AspNetUsers_UsePrimaryPhoneForRentPayouts] DEFAULT(0);
                END

                IF COL_LENGTH('AspNetUsers', 'PayoutPhoneNumber') IS NULL
                BEGIN
                    ALTER TABLE [AspNetUsers]
                    ADD [PayoutPhoneNumber] nvarchar(max) NULL;
                END

                IF COL_LENGTH('AspNetUsers', 'PayoutChannel') IS NULL
                BEGIN
                    ALTER TABLE [AspNetUsers]
                    ADD [PayoutChannel] int NULL;
                END

                IF COL_LENGTH('AspNetUsers', 'IsPayoutPhoneVerified') IS NULL
                BEGIN
                    ALTER TABLE [AspNetUsers]
                    ADD [IsPayoutPhoneVerified] bit NOT NULL
                        CONSTRAINT [DF_AspNetUsers_IsPayoutPhoneVerified] DEFAULT(0);
                END

                IF COL_LENGTH('AspNetUsers', 'PayoutPhoneVerifiedAt') IS NULL
                BEGIN
                    ALTER TABLE [AspNetUsers]
                    ADD [PayoutPhoneVerifiedAt] datetimeoffset NULL;
                END

                IF COL_LENGTH('AspNetUsers', 'WhatsAppPhoneNumber') IS NULL
                BEGIN
                    ALTER TABLE [AspNetUsers]
                    ADD [WhatsAppPhoneNumber] nvarchar(max) NULL;
                END

                IF COL_LENGTH('AspNetUsers', 'IsWhatsAppPhoneVerified') IS NULL
                BEGIN
                    ALTER TABLE [AspNetUsers]
                    ADD [IsWhatsAppPhoneVerified] bit NOT NULL
                        CONSTRAINT [DF_AspNetUsers_IsWhatsAppPhoneVerified] DEFAULT(0);
                END

                IF COL_LENGTH('AspNetUsers', 'WhatsAppPhoneVerifiedAt') IS NULL
                BEGIN
                    ALTER TABLE [AspNetUsers]
                    ADD [WhatsAppPhoneVerifiedAt] datetimeoffset NULL;
                END
            ");

            context.Database.ExecuteSqlRaw(@"
                UPDATE [AspNetUsers]
                SET
                    [SubscriptionPaymentPhoneNumber] = [PayoutPhoneNumber],
                    [SubscriptionPaymentChannel] = [PayoutChannel],
                    [IsSubscriptionPaymentPhoneVerified] = [IsPayoutPhoneVerified],
                    [SubscriptionPaymentPhoneVerifiedAt] = [PayoutPhoneVerifiedAt]
                WHERE [SubscriptionPaymentPhoneNumber] IS NULL
                  AND [PayoutPhoneNumber] IS NOT NULL;

                UPDATE [AspNetUsers]
                SET
                    [PhoneNumber] = [PayoutPhoneNumber],
                    [PhoneNumberConfirmed] = [IsPayoutPhoneVerified]
                WHERE [PhoneNumber] IS NULL
                  AND [PayoutPhoneNumber] IS NOT NULL;
            ");
        }

        private static void EnsureRentPaymentReceiptColumns(ApplicationDbContext context)
        {
            context.Database.ExecuteSqlRaw(@"
IF COL_LENGTH('Payments', 'ProviderReceiptUrl') IS NULL
BEGIN
    ALTER TABLE [Payments] ADD [ProviderReceiptUrl] nvarchar(2048) NULL;
END

IF COL_LENGTH('Payments', 'SystemReceiptNumber') IS NULL
BEGIN
    ALTER TABLE [Payments] ADD [SystemReceiptNumber] nvarchar(64) NULL;
END

IF COL_LENGTH('Payments', 'ReceiptVerificationCode') IS NULL
BEGIN
    ALTER TABLE [Payments] ADD [ReceiptVerificationCode] nvarchar(64) NULL;
END

IF COL_LENGTH('Payments', 'ReceiptIssuedAt') IS NULL
BEGIN
    ALTER TABLE [Payments] ADD [ReceiptIssuedAt] datetimeoffset NULL;
END
");

            context.Database.ExecuteSqlRaw(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_Payments_SystemReceiptNumber' AND [object_id] = OBJECT_ID(N'[Payments]'))
BEGIN
    CREATE UNIQUE INDEX [IX_Payments_SystemReceiptNumber]
    ON [Payments]([SystemReceiptNumber])
    WHERE [SystemReceiptNumber] IS NOT NULL AND [SystemReceiptNumber] <> N'' AND [IsDeleted] = 0;
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_Payments_ReceiptVerificationCode' AND [object_id] = OBJECT_ID(N'[Payments]'))
BEGIN
    CREATE UNIQUE INDEX [IX_Payments_ReceiptVerificationCode]
    ON [Payments]([ReceiptVerificationCode])
    WHERE [ReceiptVerificationCode] IS NOT NULL AND [ReceiptVerificationCode] <> N'' AND [IsDeleted] = 0;
END
");
        }

        private static void EnsureSubscriptionCheckoutColumns(ApplicationDbContext context)
        {
            context.Database.ExecuteSqlRaw(@"
                IF COL_LENGTH('UserSubscriptions', 'PaymentMethod') IS NULL
                BEGIN
                    ALTER TABLE [UserSubscriptions]
                    ADD [PaymentMethod] int NULL;
                END

                IF COL_LENGTH('UserSubscriptions', 'PaymentStatus') IS NULL
                BEGIN
                    ALTER TABLE [UserSubscriptions]
                    ADD [PaymentStatus] int NOT NULL
                        CONSTRAINT [DF_UserSubscriptions_PaymentStatus] DEFAULT(0);
                END

                IF COL_LENGTH('UserSubscriptions', 'PaymentReference') IS NULL
                BEGIN
                    ALTER TABLE [UserSubscriptions]
                    ADD [PaymentReference] nvarchar(max) NOT NULL
                        CONSTRAINT [DF_UserSubscriptions_PaymentReference] DEFAULT('');
                END

                IF COL_LENGTH('UserSubscriptions', 'PaymentProviderTransactionId') IS NULL
                BEGIN
                    ALTER TABLE [UserSubscriptions]
                    ADD [PaymentProviderTransactionId] nvarchar(max) NULL;
                END

                IF COL_LENGTH('UserSubscriptions', 'PaymentAuthorizationUrl') IS NULL
                BEGIN
                    ALTER TABLE [UserSubscriptions]
                    ADD [PaymentAuthorizationUrl] nvarchar(max) NULL;
                END

                IF COL_LENGTH('UserSubscriptions', 'AllowAutomaticCardPayments') IS NULL
                BEGIN
                    ALTER TABLE [UserSubscriptions]
                    ADD [AllowAutomaticCardPayments] bit NOT NULL
                        CONSTRAINT [DF_UserSubscriptions_AllowAutomaticCardPayments] DEFAULT(0);
                END

                IF COL_LENGTH('UserSubscriptions', 'StripeCustomerId') IS NULL
                BEGIN
                    ALTER TABLE [UserSubscriptions]
                    ADD [StripeCustomerId] nvarchar(128) NULL;
                END

                IF COL_LENGTH('UserSubscriptions', 'StripePaymentMethodId') IS NULL
                BEGIN
                    ALTER TABLE [UserSubscriptions]
                    ADD [StripePaymentMethodId] nvarchar(128) NULL;
                END

                IF COL_LENGTH('UserSubscriptions', 'IsAutomaticRenewal') IS NULL
                BEGIN
                    ALTER TABLE [UserSubscriptions]
                    ADD [IsAutomaticRenewal] bit NOT NULL
                        CONSTRAINT [DF_UserSubscriptions_IsAutomaticRenewal] DEFAULT(0);
                END

                IF COL_LENGTH('UserSubscriptions', 'LastAutomaticPaymentAttemptAt') IS NULL
                BEGIN
                    ALTER TABLE [UserSubscriptions]
                    ADD [LastAutomaticPaymentAttemptAt] datetimeoffset NULL;
                END

                IF COL_LENGTH('UserSubscriptions', 'AutomaticPaymentFailureReason') IS NULL
                BEGIN
                    ALTER TABLE [UserSubscriptions]
                    ADD [AutomaticPaymentFailureReason] nvarchar(1024) NULL;
                END

                IF COL_LENGTH('UserSubscriptions', 'PaymentCompletedAt') IS NULL
                BEGIN
                    ALTER TABLE [UserSubscriptions]
                    ADD [PaymentCompletedAt] datetimeoffset NULL;
                END

                IF COL_LENGTH('UserSubscriptions', 'PaymentAttemptCount') IS NULL
                BEGIN
                    ALTER TABLE [UserSubscriptions]
                    ADD [PaymentAttemptCount] int NOT NULL
                        CONSTRAINT [DF_UserSubscriptions_PaymentAttemptCount] DEFAULT(0);
                END

                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE name = 'IX_UserSubscriptions_AutomaticRenewalDue'
                      AND object_id = OBJECT_ID('UserSubscriptions')
                )
                BEGIN
                    CREATE INDEX [IX_UserSubscriptions_AutomaticRenewalDue]
                    ON [UserSubscriptions] ([AllowAutomaticCardPayments], [EndDate])
                    WHERE [IsDeleted] = 0;
                END
            ");

            context.Database.ExecuteSqlRaw(@"
                UPDATE [UserSubscriptions]
                SET [PaymentStatus] = CASE
                    WHEN [IsApproved] = 1 AND [PaymentStatus] = 0 THEN 1
                    ELSE [PaymentStatus]
                END
            ");
        }

        private static void EnsureSystemTransferAccountsTable(ApplicationDbContext context)
        {
            context.Database.ExecuteSqlRaw(@"
                IF OBJECT_ID('SystemTransferAccounts', 'U') IS NULL
                BEGIN
                    CREATE TABLE [SystemTransferAccounts]
                    (
                        [Id] int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                        [Channel] int NOT NULL,
                        [AccountName] nvarchar(160) NOT NULL
                            CONSTRAINT [DF_SystemTransferAccounts_AccountName] DEFAULT(''),
                        [PhoneNumber] nvarchar(40) NOT NULL
                            CONSTRAINT [DF_SystemTransferAccounts_PhoneNumber] DEFAULT(''),
                        [CountryCode] nvarchar(8) NOT NULL
                            CONSTRAINT [DF_SystemTransferAccounts_CountryCode] DEFAULT('+237'),
                        [Notes] nvarchar(280) NULL,
                        [IsDeleted] bit NOT NULL
                            CONSTRAINT [DF_SystemTransferAccounts_IsDeleted] DEFAULT(0),
                        [CreatedBy] nvarchar(450) NULL,
                        [CreatedAt] datetimeoffset NOT NULL
                            CONSTRAINT [DF_SystemTransferAccounts_CreatedAt] DEFAULT(SYSDATETIMEOFFSET()),
                        [UpdatedBy] nvarchar(450) NULL,
                        [UpdatedAt] datetimeoffset NULL,
                        [DeletedBy] nvarchar(450) NULL,
                        [DeletedAt] datetimeoffset NULL
                    );
                END

                IF NOT EXISTS (
                    SELECT 1
                    FROM sys.indexes
                    WHERE [name] = 'IX_SystemTransferAccounts_Channel'
                      AND [object_id] = OBJECT_ID('[SystemTransferAccounts]')
                )
                BEGIN
                    CREATE UNIQUE INDEX [IX_SystemTransferAccounts_Channel]
                    ON [SystemTransferAccounts]([Channel]);
                END
            ");

            context.Database.ExecuteSqlRaw(@"
                IF NOT EXISTS (SELECT 1 FROM [SystemTransferAccounts] WHERE [Channel] = 1)
                BEGIN
                    INSERT INTO [SystemTransferAccounts] ([Channel], [AccountName], [PhoneNumber], [CountryCode], [Notes], [CreatedAt], [IsDeleted])
                    VALUES (1, '', '', '+237', NULL, SYSDATETIMEOFFSET(), 0);
                END

                IF NOT EXISTS (SELECT 1 FROM [SystemTransferAccounts] WHERE [Channel] = 2)
                BEGIN
                    INSERT INTO [SystemTransferAccounts] ([Channel], [AccountName], [PhoneNumber], [CountryCode], [Notes], [CreatedAt], [IsDeleted])
                    VALUES (2, '', '', '+237', NULL, SYSDATETIMEOFFSET(), 0);
                END
            ");
        }

        private static void EnsureConversationTables(ApplicationDbContext context)
        {
            context.Database.ExecuteSqlRaw(@"
                IF OBJECT_ID('[ApartmentConversations]', 'U') IS NULL
                BEGIN
                    CREATE TABLE [ApartmentConversations]
                    (
                        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ApartmentConversations] PRIMARY KEY,
                        [ApartmentId] int NOT NULL,
                        [LandlordId] nvarchar(450) NOT NULL,
                        [VisitorId] nvarchar(450) NOT NULL,
                        [CreatedAt] datetimeoffset NOT NULL,
                        [LastMessageAt] datetimeoffset NOT NULL,
                        [LastVisitorMessageAt] datetimeoffset NULL,
                        [LastLandlordMessageAt] datetimeoffset NULL,
                        [VisitorLastReadAt] datetimeoffset NULL,
                        [LandlordLastReadAt] datetimeoffset NULL,
                        CONSTRAINT [FK_ApartmentConversations_Apartments_ApartmentId]
                            FOREIGN KEY ([ApartmentId]) REFERENCES [Apartments]([Id]) ON DELETE CASCADE,
                        CONSTRAINT [FK_ApartmentConversations_AspNetUsers_LandlordId]
                            FOREIGN KEY ([LandlordId]) REFERENCES [AspNetUsers]([Id]),
                        CONSTRAINT [FK_ApartmentConversations_AspNetUsers_VisitorId]
                            FOREIGN KEY ([VisitorId]) REFERENCES [AspNetUsers]([Id])
                    );

                    CREATE UNIQUE INDEX [IX_ApartmentConversations_ApartmentId_VisitorId]
                        ON [ApartmentConversations]([ApartmentId], [VisitorId]);

                    CREATE INDEX [IX_ApartmentConversations_LandlordId]
                        ON [ApartmentConversations]([LandlordId]);

                    CREATE INDEX [IX_ApartmentConversations_VisitorId]
                        ON [ApartmentConversations]([VisitorId]);
                END
            ");

            context.Database.ExecuteSqlRaw(@"
                IF OBJECT_ID('[ConversationMessages]', 'U') IS NULL
                BEGIN
                    CREATE TABLE [ConversationMessages]
                    (
                        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ConversationMessages] PRIMARY KEY,
                        [ConversationId] int NOT NULL,
                        [SenderId] nvarchar(450) NOT NULL,
                        [Body] nvarchar(1500) NOT NULL,
                        [CreatedAt] datetimeoffset NOT NULL,
                        CONSTRAINT [FK_ConversationMessages_ApartmentConversations_ConversationId]
                            FOREIGN KEY ([ConversationId]) REFERENCES [ApartmentConversations]([Id]) ON DELETE CASCADE,
                        CONSTRAINT [FK_ConversationMessages_AspNetUsers_SenderId]
                            FOREIGN KEY ([SenderId]) REFERENCES [AspNetUsers]([Id])
                    );

                    CREATE INDEX [IX_ConversationMessages_ConversationId]
                        ON [ConversationMessages]([ConversationId]);
                END
            ");
        }
    }
}



