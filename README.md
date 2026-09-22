# Lontsi Homes

A property and rent management application for landlords, property managers, and tenants.

## Features

- Property, apartment, tenancy, and member management.
- Monthly rent periods grouped by the lease's payment frequency.
- Automatic scheduling for open-ended tenancies and rent reminders.
- Manual payment recording, correction history, and verifiable PDF receipts.
- Tenant and landlord conversations, notifications, and document storage.
- Subscription management and integrations for payments, email, SMS, and WhatsApp.
- English and French interfaces.

## Architecture

| Component | Responsibility |
| --- | --- |
| `RentHub.Portal` | ASP.NET Core MVC interface, authentication cookies, and SignalR |
| `RentHub.API` | ASP.NET Core API, Identity, JWT authentication, and application services |
| `Common` | Shared contracts, scheduling rules, and receipt generation |
| SQL Server / EF Core | Relational storage and schema migrations |
| Hangfire | Scheduled rent-period generation, reminders, and notification delivery |
| Azure Blob Storage | Document storage |
| Docker Compose / Caddy | VPS deployment and HTTPS reverse proxy |

The product and repository are named **Lontsi Homes**. Existing `RentHub.*` code namespaces,
assembly names, database names, and deployment service identifiers are retained for
compatibility with running installations. Open **`LontsiHomes.sln`** in Visual Studio.

## Local setup

1. Install the .NET 9 SDK and provide a development SQL Server instance.
2. Create ignored `RentHub.API/appsettings.Development.json` and
   `RentHub.Portal/appsettings.Development.json` files, or use environment variables.
   Never place credentials in the tracked `appsettings.json` files.
3. Supply `ConnectionStrings:DefaultConnection` and a randomly generated
   `JwtSettings:SigningKey` to the API. Configure storage and the providers you intend to use.
   The tracked configuration contains non-secret defaults; payment and messaging integrations
   require your own provider accounts and settings.
4. Point the portal's `Api:BaseUrl` at the API. The included launch profile uses
   `https://localhost:64583/api/`.
5. Build and run:

```sh
dotnet restore LontsiHomes.sln
dotnet build LontsiHomes.sln -c Release
dotnet run --project RentHub.API
# In another terminal:
dotnet run --project RentHub.Portal
```

The API applies migrations at startup. Use a dedicated development database.

### Administrator setup

There is no built-in administrator password. Administrator creation is disabled by default.
For initial setup, provide `AdminSeed__Enabled=true`, `AdminSeed__Email`, and
`AdminSeed__Password` through private environment configuration. After creation, disable
seeding and remove the bootstrap password. Existing administrator accounts remain in the
database; changing the bootstrap setting does not rotate their passwords.

### Deployment

See [the VPS guide](deploy/vps/README.md). Copy
[`deploy/vps/.env.production.example`](deploy/vps/.env.production.example) to the ignored
`.env.production` file and supply your own values, including the public business contact
information. Keep database backups, provider keys, and private certificates outside Git.

## Security

Read [SECURITY.md](SECURITY.md) before publishing a fork or reporting a vulnerability.
