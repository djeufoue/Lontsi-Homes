# Lontsi Homes VPS Deployment

For the WhatsApp update on an **existing VPS**, follow
[WHATSAPP_PRODUCTION.md](WHATSAPP_PRODUCTION.md). It includes secret/webhook setup
and `bash deploy-update.sh` (verified backup, startup migrations, schema checks).
Do not overwrite the existing production `.env.production` with the example file.

## Updating an existing installation after the project rename

Keep the existing checkout directory and Docker Compose project name. The
`/opt/lontsihomes` paths below are examples for new installations. Changing the
Compose project name can select new, empty data volumes instead of the existing
database. Retain the existing volume names and production values for
`SQL_DATABASE`, JWT issuer/audience, and Azure storage containers.

The renamed projects and Docker entry points are updated together. Existing
authentication cookies, Identity verification tokens, and stored background-job
types retain explicit compatibility with the previous application identifiers.

This deployment target is designed for a single low-cost Linux VPS that runs:

- `LontsiHomes.Portal` (public website and workspace UI)
- `LontsiHomes.API` (backend API)
- `SQL Server 2022 Express`
- `Caddy` as the HTTPS reverse proxy

Blob/file storage stays on Azure Blob Storage.

## Recommended VPS

For a first low-cost production release, a good target is:

- `OVHcloud VPS-2`
- `6 vCores`
- `12 GB RAM`
- `100 GB SSD NVMe`
- daily backup included

As of 2026-03-23, OVHcloud advertises VPS-1 starting at `$4.20/month` on its US site:

- https://us.ovhcloud.com/vps/

Another strong option is Hetzner `CX33` / `CPX32`, but SQL Server requires `x64`, so avoid ARM plans.

## Buy the VPS

Choose these settings:

- OS: `Ubuntu 24.04 LTS x64`
- Region: closest stable EU location
- Public IPv4: included
- SSH key auth: preferred over password-only access

## Prepare the server

Run these commands after connecting by SSH as `root`:

```bash
apt update && apt upgrade -y
apt install -y ca-certificates curl git ufw

curl -fsSL https://get.docker.com | sh
systemctl enable docker
systemctl start docker

apt install -y docker-compose-plugin
```

Create a deploy user:

```bash
adduser lontsihomes
usermod -aG docker lontsihomes
```

## Firewall

Open only what you need:

```bash
ufw allow OpenSSH
ufw allow 80/tcp
ufw allow 443/tcp
ufw enable
```

Do not expose port `1433` publicly.

## Copy the project

```bash
mkdir -p /opt/lontsihomes
cd /opt/lontsihomes
git clone <your-repo-url> .
```

## Configure production secrets

Create the production env file:

```bash
cd /opt/lontsihomes/deploy/vps
cp .env.production.example .env.production
nano .env.production
```

Fill in:

- real domains
- SQL password
- JWT signing key
- Azure Blob connection string
- Infobip API key, SMS sender, WhatsApp sender, and webhook secret
- SMTP secrets
- Google Geocoding API key (server-side only)
- Stripe publishable key, secret key, and webhook signing secret
- USD to XAF conversion rate
- optional initial admin seed credentials (`ADMIN_SEED_ENABLED=true` for first setup only)

For property address geocoding, enable only the **Geocoding API** in Google Cloud and set:

```dotenv
GOOGLE_GEOCODING_API_KEY=your-production-server-key
```

This key is consumed by `LontsiHomes.API`; it is never embedded in the browser map. Restrict it to the VPS public IP and restrict its API scope to **Geocoding API**. Do not use an HTTP-referrer browser key for this variable.

The embedded property map remains OpenStreetMap. If an older property already contains incorrect coordinates, open its property overview after deployment and use **Refresh from saved address** once the key is configured.

After initial administrator creation, set `ADMIN_SEED_ENABLED=false` and remove
`ADMIN_SEED_PASSWORD` from the private environment file. Existing accounts are preserved;
changing that variable does not reset an existing account password.

## Domain DNS

Point your DNS records to the VPS public IP:

- `A lontsihomes.example.com -> <server-ip>`
- `A api.lontsihomes.example.com -> <server-ip>`

Caddy will automatically provision HTTPS once DNS resolves correctly.

## Build and start

From the VPS:

```bash
cd /opt/lontsihomes/deploy/vps
docker compose -f docker-compose.prod.yml --env-file .env.production up -d --build
```

Check status:

```bash
docker compose -f docker-compose.prod.yml ps
docker compose -f docker-compose.prod.yml logs -f api
docker compose -f docker-compose.prod.yml logs -f portal
docker compose -f docker-compose.prod.yml logs -f caddy
```

## Update deployment

Use the update script so the database is backed up before applying new columns:

```bash
cd /opt/lontsihomes
git pull --ff-only
cd deploy/vps
bash deploy-update.sh
```

The script builds first, stops the API and portal, backs up and verifies the database,
then starts the new version and waits for API readiness. Startup applies EF migrations
before serving requests. The script checks both WhatsApp and payment correction schemas.
There is a maintenance window while the API and portal are stopped.

The payment correction release includes migration
`20260914214102_AddManualPaymentCorrections`: five nullable columns and a non-unique
index on `Payments`. It does not delete or rewrite existing payments. Deploy the API
and portal together. An API image using these fields needs this migration to succeed.

If the update fails or `api` remains unhealthy, collect the actual startup error:

```bash
docker compose -f docker-compose.prod.yml --env-file .env.production logs --tail 200 api
docker compose -f docker-compose.prod.yml --env-file .env.production ps
```

Missing columns, a duplicate column/index, SQL connection errors and timeouts require
different fixes; do not mark a migration as applied or delete database volumes to
bypass the error. Keep the verified backup and error logs before attempting recovery.

## Backups

The SQL data lives in the named Docker volume `sqlserver_data`.

For better safety, add a periodic database backup job later and copy `.bak` files to Azure Blob Storage.

## Notes

- SQL Server Express is capped, but fine for a first release.
- `Portal` persists ASP.NET data protection keys in `portal_keys` to avoid invalidating all logins on each redeploy.
- `API` still performs EF migrations at startup, so the first boot needs a working SQL connection.
- The API validates every EF-mapped table/column before opening port 8080. If a production
  database has an inconsistent migration history, the API stays unhealthy and its logs list
  the exact missing columns instead of failing later during a user request.
- Portal and Caddy wait for the API health check. After an update, verify that `api` is
  `healthy` before considering the deployment complete.

Useful post-deployment checks:

```bash
docker compose -f docker-compose.prod.yml --env-file .env.production ps
docker compose -f docker-compose.prod.yml --env-file .env.production logs --tail 200 api
```

Do not manually insert rows into `__EFMigrationsHistory` or mark a migration as applied unless
its SQL changes have actually completed.
