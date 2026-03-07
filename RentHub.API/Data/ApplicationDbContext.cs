using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RentHub.API.Models.Entities;

namespace RentHub.API.Data
{
    /// <summary>
    /// Central database context for the RentHub server.  It derives from
    /// IdentityDbContext to include ASP.NET Core Identity tables for users and roles.
    /// </summary>
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, string>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        // Domain entities
        public DbSet<Property> Properties => Set<Property>();
        public DbSet<Apartment> Apartments => Set<Apartment>();
        public DbSet<Tenancy> Tenancies => Set<Tenancy>();
        public DbSet<TenancyMember> TenancyMembers => Set<TenancyMember>();
        public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
        public DbSet<UserSubscription> UserSubscriptions => Set<UserSubscription>();
        public DbSet<Payment> Payments => Set<Payment>();
        public DbSet<Document> Documents => Set<Document>();

        // Extension requests and settings
        public DbSet<TenancyExtensionRequest> TenancyExtensionRequests => Set<TenancyExtensionRequest>();
        public DbSet<ReminderSettings> ReminderSettings => Set<ReminderSettings>();

        // Delegated user assignments
        public DbSet<ApartmentOwner> ApartmentOwners => Set<ApartmentOwner>();
        public DbSet<PropertyManagerAssignment> PropertyManagerAssignments => Set<PropertyManagerAssignment>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            // Fluent API configurations can be added here. For example, restrict delete behavior to avoid cascade loops.
            builder.Entity<Property>()
                .HasMany(p => p.Apartments)
                .WithOne(a => a.Property)
                .HasForeignKey(a => a.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<Tenancy>()
                .HasOne(t => t.Apartment)
                .WithMany()
                .HasForeignKey(t => t.ApartmentId);

            builder.Entity<Tenancy>()
                .HasOne(t => t.Tenant)
                .WithMany()
                .HasForeignKey(t => t.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            // Ensure that each owner is assigned only once per apartment
            builder.Entity<ApartmentOwner>()
                .HasIndex(o => new { o.ApartmentId, o.OwnerId })
                .IsUnique();

            // Ensure that each manager is assigned only once per property
            builder.Entity<PropertyManagerAssignment>()
                .HasIndex(m => new { m.PropertyId, m.ManagerId })
                .IsUnique();

            // Apply global query filters to soft-delete entities
            builder.Entity<Property>().HasQueryFilter(p => !p.IsDeleted);
            builder.Entity<Apartment>().HasQueryFilter(a => !a.IsDeleted);
            builder.Entity<Tenancy>().HasQueryFilter(t => !t.IsDeleted);
            builder.Entity<TenancyMember>().HasQueryFilter(tm => !tm.IsDeleted);
            builder.Entity<SubscriptionPlan>().HasQueryFilter(sp => !sp.IsDeleted);
            builder.Entity<UserSubscription>().HasQueryFilter(us => !us.IsDeleted);
            builder.Entity<Payment>().HasQueryFilter(p => !p.IsDeleted);
            builder.Entity<Document>().HasQueryFilter(d => !d.IsDeleted);
            builder.Entity<ApartmentOwner>().HasQueryFilter(o => !o.IsDeleted);
            builder.Entity<PropertyManagerAssignment>().HasQueryFilter(m => !m.IsDeleted);
            builder.Entity<ReminderSettings>().HasQueryFilter(rs => !rs.IsDeleted);

            // Apply soft-delete filter to extension requests if desired
            builder.Entity<TenancyExtensionRequest>().HasQueryFilter(er => !er.IsDeleted);
        }
    }
}