using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Hangfire;
using Hangfire.SqlServer;
using System.Text;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.HttpOverrides;

using RentHub.API.Data;
using RentHub.API.Models.Entities;
using RentHub.API.Models.Settings;
using RentHub.API.Middleware;
using RentHub.API.Services.Payments;
using RentHub.API.Services.Storage;
using RentHub.API.Services.Sms;
using RentHub.API.Services.Email;
using RentHub.API.Services.Reminders;
using RentHub.API.Services.Tenancies;
using RentHub.API.Services.Users;
using RentHub.API.Services.Kyc;
using RentHub.API.Services.Subscriptions;
using RentHub.API.Services.Receipts;
using RentHub.API.Services.Conversations;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Controllers & endpoints
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHttpClient();

// Swagger (register a v1 doc + JWT auth button)
builder.Services.AddSwaggerGen(c =>
{
    c.CustomSchemaIds(type => type.FullName);

    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "RentHub API",
        Version = "v1",
        Description = "Property & rent management API"
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Enter: Bearer {your_token}"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
});

// Database
ConfigureDatabase(builder.Services, builder.Configuration, builder.Environment.IsDevelopment());

// Identity
ConfigureIdentity(builder.Services);

// Bind & validate JWT settings at startup
builder.Services.AddOptions<JwtSettings>()
    .Bind(builder.Configuration.GetSection("JwtSettings"))
    .Validate(s => !string.IsNullOrWhiteSpace(s.Issuer), "JwtSettings.Issuer is required")
    .Validate(s => !string.IsNullOrWhiteSpace(s.Audience), "JwtSettings.Audience is required")
    .Validate(s => !string.IsNullOrWhiteSpace(s.SigningKey), "JwtSettings.SigningKey is required")
    .ValidateOnStart();

// Authentication (JWT)
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer((options) =>
{
    var jwtSettings = builder.Configuration.GetSection("JwtSettings").Get<JwtSettings>();
    if (jwtSettings is null)
        throw new InvalidOperationException("Missing 'JwtSettings' section in appsettings.");

    // Map standard JWT claims (sub/nameid/role) to ClaimTypes so User.FindFirstValue(ClaimTypes.NameIdentifier) works consistently.
    options.MapInboundClaims = true;


    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = context =>
        {
            if (context.Principal?.Identity is ClaimsIdentity identity)
            {
                var existingUserId = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrWhiteSpace(existingUserId))
                {
                    var fallbackUserId =
                        identity.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ??
                        identity.FindFirst(JwtRegisteredClaimNames.NameId)?.Value ??
                        identity.FindFirst("nameid")?.Value;

                    if (!string.IsNullOrWhiteSpace(fallbackUserId))
                    {
                        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, fallbackUserId));
                    }
                }
            }

            return Task.CompletedTask;
        }
    };

    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSettings.Issuer,
        ValidAudience = jwtSettings.Audience,
        NameClaimType = ClaimTypes.Name,
        RoleClaimType = ClaimTypes.Role,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SigningKey))
    };
});

// Domain services
builder.Services.AddScoped<IPaymentService, OrangeMoneyService>();
builder.Services.AddScoped<INotchPayService, NotchPayService>();
builder.Services.AddScoped<ICamPayService, CamPayService>();
builder.Services.AddScoped<IStripeCheckoutService, StripeCheckoutService>();
builder.Services.AddScoped<IStripeConnectService, StripeConnectService>();
builder.Services.AddScoped<ISubscriptionAutoRenewalService, SubscriptionAutoRenewalService>();
builder.Services.AddScoped<MomoService>();
builder.Services.AddScoped<CardPaymentService>();
builder.Services.AddScoped<IStorageService, AzureStorageService>();
builder.Services.AddScoped<IKycFileStorageService, BlobKycFileStorageService>();
builder.Services.AddScoped<RentHub.API.Services.Auth.TokenService>();
builder.Services.AddScoped<IUserOnboardingService, UserOnboardingService>();
builder.Services.AddScoped<IManagerInvitationEmailService, ManagerInvitationEmailService>();
builder.Services.AddScoped<RentHub.API.Services.Permissions.IManagerPermissionService, RentHub.API.Services.Permissions.ManagerPermissionService>();
builder.Services.AddScoped<RentHub.API.Services.Maps.IPropertyGeocodingService, RentHub.API.Services.Maps.GooglePropertyGeocodingService>();
builder.Services.AddScoped<IRentReceiptService, RentReceiptService>();
builder.Services.AddScoped<ITenancyRenewalEmailService, TenancyRenewalEmailService>();
builder.Services.AddScoped<ITenancyTerminationEmailService, TenancyTerminationEmailService>();
builder.Services.AddScoped<IOpenEndedTenancyRentPeriodService, OpenEndedTenancyRentPeriodService>();
builder.Services.AddScoped<IRentReminderService, RentReminderService>();
builder.Services.AddScoped<IConversationNotificationJob, ConversationNotificationJob>();

// SMS & Email
builder.Services.AddScoped<ISmsService, TwilioSmsService>();
if (!string.IsNullOrWhiteSpace(builder.Configuration["Email:Smtp:Host"]))
{
    builder.Services.AddScoped<IEmailService, SmtpEmailService>();
}
else
{
    builder.Services.AddScoped<IEmailService, StubEmailService>();
}

ConfigureHangfire(builder.Services, builder.Configuration);

var app = builder.Build();

// Seed initial data
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    SeedData.Initialize(services, builder.Configuration);
}

// Pipeline
app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
app.UseMiddleware<RequestUserLoggingMiddleware>();
app.UseAuthorization();

// Swagger UI (you can keep this always-on while developing)
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "RentHub API v1");
    // Optional: show Swagger at root instead of /swagger
    // c.RoutePrefix = string.Empty;
});

if (app.Environment.IsDevelopment())
{
    app.UseHangfireDashboard("/hangfire");
}

RegisterRecurringJobs(app.Services, builder.Configuration);

// Optional: redirect root to Swagger if you didn't set RoutePrefix = string.Empty
app.MapGet("/", () => Results.Redirect("/swagger"));

app.MapControllers();
app.Run();


// ---------- local helpers ----------
void ConfigureDatabase(IServiceCollection services, IConfiguration configuration, bool isDevelopment)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? "Server=(localdb)\\mssqllocaldb;Database=RentHubDb;Trusted_Connection=True;MultipleActiveResultSets=true";

    var enableSensitiveDataLogging = configuration.GetValue<bool>("EfCore:EnableSensitiveDataLogging");

    services.AddDbContext<ApplicationDbContext>(options =>
    {
        options.UseSqlServer(connectionString);
        options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

        if (isDevelopment && enableSensitiveDataLogging)
        {
            options.EnableSensitiveDataLogging();
        }
    });
}

void ConfigureIdentity(IServiceCollection services)
{
    services.AddIdentity<ApplicationUser, ApplicationRole>()
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddDefaultTokenProviders();

    services.Configure<IdentityOptions>(options =>
    {
        options.Password.RequiredLength = 6;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
    });
}

void ConfigureHangfire(IServiceCollection services, IConfiguration configuration)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? "Server=(localdb)\\mssqllocaldb;Database=RentHubDb;Trusted_Connection=True;MultipleActiveResultSets=true";

    services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
        {
            CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
            QueuePollInterval = TimeSpan.FromSeconds(15),
            UseRecommendedIsolationLevel = true,
            DisableGlobalLocks = true
        }));

    services.AddHangfireServer();
}

void RegisterRecurringJobs(IServiceProvider services, IConfiguration configuration)
{
    using var scope = services.CreateScope();
    var recurringJobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    recurringJobs.AddOrUpdate<IOpenEndedTenancyRentPeriodService>(
        "open-ended-tenancy-rent-period-provisioning",
        service => service.EnsureAllOpenEndedTenancyPeriodsAsync(),
        Cron.Daily(),
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });

    // Period provisioning runs first at midnight. Reminder processing starts
    // five minutes later so newly-created open-ended periods are already visible.
    recurringJobs.AddOrUpdate<IRentReminderService>(
        "rent-reminder-processing",
        service => service.ProcessDailyRemindersAsync(),
        "5 0 * * *",
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });

    // Run an idempotent catch-up after each deployment/startup so existing
    // open-ended tenancies do not need to wait until the next midnight cycle.
    var backgroundJobs = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
    var provisioningJobId = backgroundJobs.Enqueue<IOpenEndedTenancyRentPeriodService>(
        service => service.EnsureAllOpenEndedTenancyPeriodsAsync());
    backgroundJobs.ContinueJobWith<IRentReminderService>(
        provisioningJobId,
        service => service.ProcessDailyRemindersAsync());

    if (configuration.GetValue<bool?>("Subscriptions:EnableAutomaticRenewalJob").GetValueOrDefault(true))
    {
        var intervalMinutes = Math.Clamp(
            configuration.GetValue<int?>("Subscriptions:AutoRenewalCheckMinutes") ?? 60,
            5,
            1440);
        var cron = intervalMinutes < 60
            ? $"*/{intervalMinutes} * * * *"
            : Cron.Hourly();

        recurringJobs.AddOrUpdate<ISubscriptionAutoRenewalService>(
            "landlord-subscription-card-renewal",
            service => service.ProcessDueRenewalsAsync(),
            cron);
    }
}







