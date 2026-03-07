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
        public static void Initialize(IServiceProvider services)
        {
            using var scope = services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            context.Database.Migrate();

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

            // Seed default administrator
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            const string adminEmail = "adriendjeufouelontsi@gmail.com";
            const string adminPassword = "Admin@12345";
            var admin = userManager.FindByEmailAsync(adminEmail).GetAwaiter().GetResult();
            if (admin == null)
            {
                admin = new ApplicationUser
                {
                    UserName = adminEmail,
                    Email = adminEmail,
                    EmailConfirmed = true,
                    FullName = "System Administrator",
                    CountryCode = "+237"
                };
                userManager.CreateAsync(admin, adminPassword).GetAwaiter().GetResult();
                userManager.AddToRoleAsync(admin, "Admin").GetAwaiter().GetResult();
            }

            // Additional seeding (roles, admin user) can be added here.
        }
    }
}