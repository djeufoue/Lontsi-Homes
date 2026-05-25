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
        public DbSet<PaymentWebhookEvent> PaymentWebhookEvents => Set<PaymentWebhookEvent>();
        public DbSet<Document> Documents => Set<Document>();
        public DbSet<SystemTransferAccount> SystemTransferAccounts => Set<SystemTransferAccount>();
        public DbSet<ApartmentConversation> ApartmentConversations => Set<ApartmentConversation>();
        public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();

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
                .WithMany(a => a.Tenancies)
                .HasForeignKey(t => t.ApartmentId);

            builder.Entity<SubscriptionPlan>()
                .Property(p => p.Price)
                .HasPrecision(18, 2);

            builder.Entity<Tenancy>()
                .Property(t => t.MonthlyRent)
                .HasPrecision(18, 2);

            builder.Entity<UserSubscription>(entity =>
            {
                entity.Property(us => us.PlanPriceSnapshot)
                    .HasPrecision(18, 2);

                entity.Property(us => us.PaymentReference)
                    .HasMaxLength(128);

                entity.Property(us => us.PaymentProviderTransactionId)
                    .HasMaxLength(128);

                entity.Property(us => us.PaymentAuthorizationUrl)
                    .HasMaxLength(2048);

                entity.HasIndex(us => us.PaymentReference)
                    .IsUnique()
                    .HasFilter("[PaymentReference] IS NOT NULL AND [PaymentReference] <> N'' AND [IsDeleted] = 0");

                entity.HasIndex(us => us.PaymentProviderTransactionId)
                    .IsUnique()
                    .HasFilter("[PaymentProviderTransactionId] IS NOT NULL AND [PaymentProviderTransactionId] <> N'' AND [IsDeleted] = 0");

                entity.HasIndex(us => new { us.UserId, us.SubscriptionPlanId })
                    .HasDatabaseName("IX_UserSubscriptions_UserId_SubscriptionPlanId_OpenPayment")
                    .IsUnique()
                    .HasFilter("[IsDeleted] = 0 AND [IsApproved] = 0 AND [PaymentStatus] <> 1");
            });

            builder.Entity<Payment>(entity =>
            {
                entity.Property(p => p.RequestKey)
                    .HasMaxLength(160);

                entity.Property(p => p.TransactionId)
                    .HasMaxLength(128);

                entity.HasIndex(p => p.RequestKey)
                    .IsUnique()
                    .HasFilter("[RequestKey] IS NOT NULL AND [RequestKey] <> N'' AND [IsDeleted] = 0");

                entity.HasIndex(p => p.TransactionId)
                    .IsUnique()
                    .HasFilter("[TransactionId] IS NOT NULL AND [TransactionId] <> N'' AND [IsDeleted] = 0");

                entity.HasIndex(p => new { p.TenancyId, p.Status, p.PaymentDate });
            });

            builder.Entity<PaymentWebhookEvent>(entity =>
            {
                entity.Property(e => e.Provider)
                    .HasMaxLength(50);

                entity.Property(e => e.EventKey)
                    .HasMaxLength(160);

                entity.Property(e => e.EventType)
                    .HasMaxLength(100);

                entity.Property(e => e.PaymentReference)
                    .HasMaxLength(128);

                entity.Property(e => e.ProviderTransactionId)
                    .HasMaxLength(128);

                entity.Property(e => e.PayloadHash)
                    .HasMaxLength(64);

                entity.Property(e => e.ProcessingStatus)
                    .HasMaxLength(30);

                entity.HasIndex(e => new { e.Provider, e.EventKey })
                    .IsUnique();

                entity.HasIndex(e => e.PaymentReference);
            });

            builder.Entity<SystemTransferAccount>()
                .HasIndex(a => a.Channel)
                .IsUnique();

            builder.Entity<ApartmentConversation>()
                .HasIndex(c => new { c.ApartmentId, c.VisitorId })
                .IsUnique();

            builder.Entity<ApartmentConversation>()
                .HasOne(c => c.Apartment)
                .WithMany()
                .HasForeignKey(c => c.ApartmentId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<ConversationMessage>()
                .HasOne(m => m.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

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
            builder.Entity<SystemTransferAccount>().HasQueryFilter(a => !a.IsDeleted);
            builder.Entity<ApartmentOwner>().HasQueryFilter(o => !o.IsDeleted);
            builder.Entity<PropertyManagerAssignment>().HasQueryFilter(m => !m.IsDeleted);
            builder.Entity<ReminderSettings>().HasQueryFilter(rs => !rs.IsDeleted);

            // Apply soft-delete filter to extension requests if desired
            builder.Entity<TenancyExtensionRequest>().HasQueryFilter(er => !er.IsDeleted);
        }
    }
}

