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
            EnsureUserVerificationColumns(context);
            EnsureSubscriptionCheckoutColumns(context);
            EnsureConversationTables(context);
            EnsureSystemTransferAccountsTable(context);

            // Seed subscription plans
            if (!context.SubscriptionPlans.Any())
            {
                context.SubscriptionPlans.AddRange(new[]
                {
                    new SubscriptionPlan {
                        Name = "Basic",
                        Price = 5000M,
                        DurationInDays = 30,
                        Description = "Advertise up to 3 properties.",
                        MaxProperties = 3,
                        MaxApartmentsPerProperty = 2
                    },
                    new SubscriptionPlan {
                        Name = "Pro",
                        Price = 15000M,
                        DurationInDays = 90,
                        Description = "Advertise up to 10 properties and manage tenants.",
                        MaxProperties = 10,
                        MaxApartmentsPerProperty = 5
                    },
                    new SubscriptionPlan {
                        Name = "Enterprise",
                        Price = 30000M,
                        DurationInDays = 365,
                        Description = "Unlimited properties with premium support.",
                        MaxProperties = null,
                        MaxApartmentsPerProperty = null
                    }
                });
                context.SaveChanges();
            }

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

        private static void EnsureUserVerificationColumns(ApplicationDbContext context)
        {
            context.Database.ExecuteSqlRaw(@"
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



