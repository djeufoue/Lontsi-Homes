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
            EnsureTenancyTenantColumnRemoved(context);
            EnsureTenancyApartmentShadowColumnRemoved(context);

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

            // Ensure roles exist (Admin, Landlord, Tenant, Owner, Manager)
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            string[] roles = new[] { "Admin", "Landlord", "Tenant", "Owner", "Manager" };
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
    }
}


