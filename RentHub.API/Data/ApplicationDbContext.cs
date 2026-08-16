using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Common.Enums;
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
        public DbSet<RentPeriod> RentPeriods => Set<RentPeriod>();
        public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
        public DbSet<UserSubscription> UserSubscriptions => Set<UserSubscription>();
        public DbSet<Payment> Payments => Set<Payment>();
        public DbSet<PaymentWebhookEvent> PaymentWebhookEvents => Set<PaymentWebhookEvent>();
        public DbSet<OtpSendLog> OtpSendLogs => Set<OtpSendLog>();
        public DbSet<LandlordKycProfile> LandlordKycProfiles => Set<LandlordKycProfile>();
        public DbSet<Document> Documents => Set<Document>();
        public DbSet<SystemTransferAccount> SystemTransferAccounts => Set<SystemTransferAccount>();
        public DbSet<PlatformPaymentSettings> PlatformPaymentSettings => Set<PlatformPaymentSettings>();
        public DbSet<ApartmentConversation> ApartmentConversations => Set<ApartmentConversation>();
        public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();
        public DbSet<SubscriptionInquiry> SubscriptionInquiries => Set<SubscriptionInquiry>();
        public DbSet<SubscriptionInquiryMessage> SubscriptionInquiryMessages => Set<SubscriptionInquiryMessage>();

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

            builder.Entity<SubscriptionPlan>()
                .Property(p => p.AnnualPrice)
                .HasPrecision(18, 2);

            builder.Entity<ApplicationUser>(entity =>
            {
                entity.Property(u => u.Language)
                    .HasDefaultValue(PlatformLanguage.English);

                entity.Property(u => u.CountryIsoCode)
                    .HasMaxLength(2);

                entity.Property(u => u.StripeConnectAccountId)
                    .HasMaxLength(128);

                entity.Property(u => u.StripePayoutRequirementsSummary)
                    .HasMaxLength(1024);

                entity.Property(u => u.StripePayoutDisabledReason)
                    .HasMaxLength(512);
            });

            builder.Entity<Property>(entity =>
            {
                entity.Property(p => p.CountryIsoCode)
                    .HasMaxLength(2);

                entity.Property(p => p.CountryCode)
                    .HasMaxLength(8);
            });

            builder.Entity<Tenancy>()
                .Property(t => t.MonthlyRent)
                .HasPrecision(18, 2);

            builder.Entity<Tenancy>(entity =>
            {
                entity.Property(t => t.TerminationNotes)
                    .HasMaxLength(512);
            });

            builder.Entity<TenancyExtensionRequest>(entity =>
            {
                entity.Property(request => request.RejectionReason)
                    .HasMaxLength(512);

                entity.HasOne(request => request.Tenancy)
                    .WithMany(tenancy => tenancy.ExtensionRequests)
                    .HasForeignKey(request => request.TenancyId);

                entity.HasIndex(request => request.TenancyId)
                    .IsUnique()
                    .HasFilter("[Status] = 0 AND [IsDeleted] = 0");
            });

            builder.Entity<RentPeriod>(entity =>
            {
                entity.Property(rp => rp.Amount)
                    .HasPrecision(14, 2);

                entity.Property(rp => rp.PaidAmount)
                    .HasPrecision(14, 2);

                entity.Property(rp => rp.PaymentReference)
                    .HasMaxLength(128);

                entity.HasOne(rp => rp.Tenancy)
                    .WithMany(t => t.RentPeriods)
                    .HasForeignKey(rp => rp.TenancyId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(rp => rp.Payment)
                    .WithMany()
                    .HasForeignKey(rp => rp.PaymentId)
                    .OnDelete(DeleteBehavior.NoAction);

                entity.HasIndex(rp => new { rp.TenancyId, rp.PeriodStart })
                    .IsUnique()
                    .HasFilter("[IsDeleted] = 0");

                entity.HasIndex(rp => new { rp.TenancyId, rp.Status, rp.DueDate });
            });

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

                entity.Property(us => us.StripeCustomerId)
                    .HasMaxLength(128);

                entity.Property(us => us.StripePaymentMethodId)
                    .HasMaxLength(128);

                entity.Property(us => us.AutomaticPaymentFailureReason)
                    .HasMaxLength(1024);

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

                entity.HasIndex(us => new { us.AllowAutomaticCardPayments, us.EndDate })
                    .HasDatabaseName("IX_UserSubscriptions_AutomaticRenewalDue");
            });

            builder.Entity<Payment>(entity =>
            {
                entity.Property(p => p.RequestKey)
                    .HasMaxLength(160);

                entity.Property(p => p.TransactionId)
                    .HasMaxLength(128);

                entity.Property(p => p.ProviderReceiptUrl)
                    .HasMaxLength(2048);

                entity.Property(p => p.SystemReceiptNumber)
                    .HasMaxLength(64);

                entity.Property(p => p.ReceiptVerificationCode)
                    .HasMaxLength(64);

                entity.HasIndex(p => p.RequestKey)
                    .IsUnique()
                    .HasFilter("[RequestKey] IS NOT NULL AND [RequestKey] <> N'' AND [IsDeleted] = 0");

                entity.HasIndex(p => p.TransactionId)
                    .IsUnique()
                    .HasFilter("[TransactionId] IS NOT NULL AND [TransactionId] <> N'' AND [IsDeleted] = 0");

                entity.HasIndex(p => p.SystemReceiptNumber)
                    .IsUnique()
                    .HasFilter("[SystemReceiptNumber] IS NOT NULL AND [SystemReceiptNumber] <> N'' AND [IsDeleted] = 0");

                entity.HasIndex(p => p.ReceiptVerificationCode)
                    .IsUnique()
                    .HasFilter("[ReceiptVerificationCode] IS NOT NULL AND [ReceiptVerificationCode] <> N'' AND [IsDeleted] = 0");

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

            builder.Entity<OtpSendLog>(entity =>
            {
                entity.Property(e => e.UserId)
                    .HasMaxLength(450);

                entity.Property(e => e.Purpose)
                    .HasMaxLength(64);

                entity.Property(e => e.Channel)
                    .HasMaxLength(20);

                entity.Property(e => e.Recipient)
                    .HasMaxLength(64);

                entity.HasOne(e => e.User)
                    .WithMany()
                    .HasForeignKey(e => e.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(e => new { e.UserId, e.Purpose, e.SentAt });
                entity.HasIndex(e => new { e.UserId, e.Purpose, e.Recipient, e.SentAt });
            });

            builder.Entity<LandlordKycProfile>(entity =>
            {
                entity.Property(e => e.UserId)
                    .HasMaxLength(450);

                entity.Property(e => e.FaceFrontPath)
                    .HasMaxLength(1024);

                entity.Property(e => e.FaceRightPath)
                    .HasMaxLength(1024);

                entity.Property(e => e.FaceLeftPath)
                    .HasMaxLength(1024);

                entity.Property(e => e.DocumentFrontPath)
                    .HasMaxLength(1024);

                entity.Property(e => e.DocumentBackPath)
                    .HasMaxLength(1024);

                entity.HasOne(e => e.User)
                    .WithOne(u => u.KycProfile)
                    .HasForeignKey<LandlordKycProfile>(e => e.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(e => e.ReviewedBy)
                    .WithMany()
                    .HasForeignKey(e => e.ReviewedById)
                    .OnDelete(DeleteBehavior.NoAction);

                entity.HasIndex(e => e.UserId)
                    .IsUnique();

                entity.HasIndex(e => new { e.Status, e.SubmittedAt });
            });

            builder.Entity<SystemTransferAccount>()
                .HasIndex(a => a.Channel)
                .IsUnique();

            builder.Entity<PlatformPaymentSettings>()
                .Property(settings => settings.Id)
                .ValueGeneratedNever();

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

            builder.Entity<SubscriptionInquiry>(entity =>
            {
                entity.Property(i => i.ProposedMonthlyPrice).HasPrecision(18, 2);
                entity.HasIndex(i => i.PublicAccessToken).IsUnique();
                entity.HasIndex(i => new { i.RequesterUserId, i.LastMessageAt });
                entity.HasOne(i => i.RequesterUser).WithMany().HasForeignKey(i => i.RequesterUserId).OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<SubscriptionInquiryMessage>(entity =>
            {
                entity.HasOne(m => m.SubscriptionInquiry).WithMany(i => i.Messages).HasForeignKey(m => m.SubscriptionInquiryId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(m => m.SenderUser).WithMany().HasForeignKey(m => m.SenderUserId).OnDelete(DeleteBehavior.NoAction);
            });

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
            builder.Entity<RentPeriod>().HasQueryFilter(rp => !rp.IsDeleted);
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

