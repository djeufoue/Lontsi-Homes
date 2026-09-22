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
using Microsoft.AspNetCore.DataProtection;

using LontsiHomes.API.Data;
using LontsiHomes.API.Models.Entities;
using LontsiHomes.API.Models.Settings;
using LontsiHomes.API.Middleware;
using LontsiHomes.API.Services.Payments;
using LontsiHomes.API.Services.Storage;
using LontsiHomes.API.Services.Email;
using LontsiHomes.API.Services.Reminders;
using LontsiHomes.API.Services.Tenancies;
using LontsiHomes.API.Services.Users;
using LontsiHomes.API.Services.Kyc;
using LontsiHomes.API.Services.Subscriptions;
using LontsiHomes.API.Services.Receipts;
using LontsiHomes.API.Services.Conversations;
using LontsiHomes.API.Services.Messaging;
using LontsiHomes.API.Services.Otp;

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
        Title = "Lontsi Homes API",
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
        OnTokenValidated = async context =>
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

                var userId = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrWhiteSpace(userId))
                {
                    context.Fail("The authenticated user identifier is missing.");
                    return;
                }

                var userManager = context.HttpContext.RequestServices
                    .GetRequiredService<UserManager<ApplicationUser>>();
                var user = await userManager.FindByIdAsync(userId);
                if (user == null)
                {
                    context.Fail("The authenticated user no longer exists.");
                    return;
                }

                var tokenSecurityStamp = identity.FindFirst("security_stamp")?.Value;
                if (!string.IsNullOrWhiteSpace(tokenSecurityStamp))
                {
                    if (!string.Equals(tokenSecurityStamp, user.SecurityStamp, StringComparison.Ordinal))
                    {
                        context.Fail("This session has been invalidated.");
                    }

                    return;
                }

                // Compatibility for JWTs issued before security-stamp claims were introduced.
                if (user.SessionInvalidatedAt.HasValue)
                {
                    var issuedAtValue = identity.FindFirst(JwtRegisteredClaimNames.Iat)?.Value ??
                                        identity.FindFirst("iat")?.Value;
                    if (!long.TryParse(issuedAtValue, out var issuedAtUnix) ||
                        DateTimeOffset.FromUnixTimeSeconds(issuedAtUnix) <= user.SessionInvalidatedAt.Value)
                    {
                        context.Fail("This session has been invalidated.");
                    }
                }
            }
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
builder.Services.AddScoped<LontsiHomes.API.Services.Auth.TokenService>();
builder.Services.AddScoped<IUserOnboardingService, UserOnboardingService>();
builder.Services.AddScoped<IManagerInvitationEmailService, ManagerInvitationEmailService>();
builder.Services.AddScoped<LontsiHomes.API.Services.Permissions.IManagerPermissionService, LontsiHomes.API.Services.Permissions.ManagerPermissionService>();
builder.Services.AddScoped<LontsiHomes.API.Services.Maps.IPropertyGeocodingService, LontsiHomes.API.Services.Maps.GooglePropertyGeocodingService>();
builder.Services.AddScoped<IRentReceiptService, RentReceiptService>();
builder.Services.AddScoped<PaymentCorrectionNotifications>();
var apiDataProtection = builder.Services.AddDataProtection().SetApplicationName("LontsiHomes.API");
var apiKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(apiKeysPath))
{
    apiDataProtection.PersistKeysToFileSystem(new DirectoryInfo(apiKeysPath));
}
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<WhatsAppReceiptMedia>();
builder.Services.AddScoped<ITenancyRenewalEmailService, TenancyRenewalEmailService>();
builder.Services.AddScoped<ITenancyTerminationEmailService, TenancyTerminationEmailService>();
builder.Services.AddScoped<IOpenEndedTenancyRentPeriodService, OpenEndedTenancyRentPeriodService>();
builder.Services.AddScoped<IRentReminderService, RentReminderService>();
builder.Services.AddScoped<IConversationNotificationJob, ConversationNotificationJob>();
builder.Services.AddScoped<INotificationDeliveryService, NotificationDeliveryService>();

// Infobip is intentionally inert until every required value is configured.
builder.Services.AddOptions<InfobipOptions>()
    .Bind(builder.Configuration.GetSection(InfobipOptions.SectionName));
builder.Services.PostConfigure<InfobipOptions>(options =>
{
    options.BindTemplateConfiguration(builder.Configuration);
    options.BaseUrl = Environment.GetEnvironmentVariable("INFOBIP_BASE_URL") ?? options.BaseUrl;
    options.ApiKey = Environment.GetEnvironmentVariable("INFOBIP_API_KEY") ?? options.ApiKey;
    options.SmsSender = Environment.GetEnvironmentVariable("INFOBIP_SMS_SENDER") ?? options.SmsSender;
    options.WhatsAppSender = Environment.GetEnvironmentVariable("INFOBIP_WHATSAPP_SENDER") ?? options.WhatsAppSender;
    options.WebhookSecret = Environment.GetEnvironmentVariable("INFOBIP_WEBHOOK_SECRET") ?? options.WebhookSecret;
});
builder.Services.AddSingleton<IOtpService, CryptographicOtpService>();
builder.Services.AddSingleton<IWhatsAppTemplateRegistry, WhatsAppTemplateRegistry>();
builder.Services.AddHttpClient<ISmsMessagingService, InfobipSmsMessagingService>((services, client) =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<InfobipOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<IWhatsAppMessagingService, InfobipWhatsAppMessagingService>((services, client) =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<InfobipOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Email
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
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Lontsi Homes API v1");
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
        ?? "Server=(localdb)\\mssqllocaldb;Database=LontsiHomesDb;Trusted_Connection=True;MultipleActiveResultSets=true";

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
        ?? "Server=(localdb)\\mssqllocaldb;Database=LontsiHomesDb;Trusted_Connection=True;MultipleActiveResultSets=true";

    services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        // Jobs already stored in SQL refer to the old assembly and namespace.
        .UseTypeResolver(typeName => Hangfire.Common.TypeHelper.DefaultTypeResolver(
            typeName.Replace("RentHub.API", "LontsiHomes.API", StringComparison.Ordinal)))
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

    recurringJobs.AddOrUpdate<INotificationDeliveryService>(
        "infobip-whatsapp-outbox",
        service => service.ProcessPendingAsync(CancellationToken.None),
        Cron.Minutely(),
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });

    recurringJobs.AddOrUpdate<PaymentCorrectionNotifications>(
        "manual-payment-correction-emails", service => service.ProcessAsync(), Cron.Minutely());

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







