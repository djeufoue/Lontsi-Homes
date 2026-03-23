# RentHub VPS Deployment

This deployment target is designed for a single low-cost Linux VPS that runs:

- `RentHub.Portal` (public website and workspace UI)
- `RentHub.API` (backend API)
- `SQL Server 2022 Express`
- `Caddy` as the HTTPS reverse proxy

Blob/file storage stays on Azure Blob Storage.

## Recommended VPS

For a first low-cost production release, a good target is:

- `OVHcloud VPS-1`
- `4 vCores`
- `8 GB RAM`
- `75 GB SSD`
- daily backup included

As of 2026-03-23, OVHcloud advertises VPS-1 starting at `$4.20/month` on its US site:

- https://us.ovhcloud.com/vps/

Another strong option is Hetzner `CX33` / `CPX32`, but SQL Server requires `x64`, so avoid ARM plans.

## Buy the VPS

Choose these settings:

- OS: `Ubuntu 22.04 LTS x64`
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
adduser renthub
usermod -aG docker renthub
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
mkdir -p /opt/renthub
cd /opt/renthub
git clone <your-repo-url> .
```

## Configure production secrets

Create the production env file:

```bash
cd /opt/renthub/deploy/vps
cp .env.production.example .env.production
nano .env.production
```

Fill in:

- real domains
- SQL password
- JWT signing key
- Azure Blob connection string
- Twilio secrets
- SMTP secrets
- Notch Pay secrets
- admin seed credentials

## Domain DNS

Point your DNS records to the VPS public IP:

- `A renthub.example.com -> <server-ip>`
- `A api.renthub.example.com -> <server-ip>`

Caddy will automatically provision HTTPS once DNS resolves correctly.

## Build and start

From the VPS:

```bash
cd /opt/renthub/deploy/vps
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

```bash
cd /opt/renthub
git pull
cd deploy/vps
docker compose -f docker-compose.prod.yml --env-file .env.production up -d --build
```

## Backups

The SQL data lives in the named Docker volume `sqlserver_data`.

For better safety, add a periodic database backup job later and copy `.bak` files to Azure Blob Storage.

## Notes

- SQL Server Express is capped, but fine for a first release.
- `Portal` persists ASP.NET data protection keys in `portal_keys` to avoid invalidating all logins on each redeploy.
- `API` still performs EF migrations at startup, so the first boot needs a working SQL connection.
