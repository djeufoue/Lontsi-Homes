using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;

using RentHub.API.Data;
using RentHub.API.Models.Entities;
using RentHub.API.Models.Settings;
using RentHub.API.Services.Payments;
using RentHub.API.Services.Storage;
using RentHub.API.Services.Sms;
using RentHub.API.Services.Email;
using RentHub.API.Services.Reminders;
using RentHub.API.Services.Users;

var builder = WebApplication.CreateBuilder(args);

// Controllers & endpoints
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

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
builder.Services.AddScoped<MomoService>();
builder.Services.AddScoped<CardPaymentService>();
builder.Services.AddScoped<IStorageService, AzureStorageService>();
builder.Services.AddScoped<RentHub.API.Services.Auth.TokenService>();
builder.Services.AddScoped<IUserOnboardingService, UserOnboardingService>();

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

// Rent reminder background worker
builder.Services.AddHostedService<RentReminderHostedService>();

var app = builder.Build();

// Seed initial data
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    SeedData.Initialize(services, builder.Configuration);
}

// Pipeline
app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Swagger UI (you can keep this always-on while developing)
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "RentHub API v1");
    // Optional: show Swagger at root instead of /swagger
    // c.RoutePrefix = string.Empty;
});

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







