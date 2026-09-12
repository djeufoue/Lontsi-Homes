#!/usr/bin/env bash
# Existing VPS only. Run explicitly after reviewing/updating .env.production.
set -Eeuo pipefail
umask 077
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
compose=(docker compose --env-file .env.production -f docker-compose.prod.yml)
trap 'printf "Deployment stopped at line %s. Check the error and API logs. Do not delete volumes or edit migration history.\n" "$LINENO" >&2' ERR
test -f .env.production
chmod 600 .env.production
"${compose[@]}" config --quiet

# Do not source .env: dotenv values (including secrets) are not shell code.
database=$(sed -n 's/^SQL_DATABASE=//p' .env.production | tr -d '\r')
if [[ ! "$database" =~ ^[A-Za-z0-9_]+$ ]]; then
    printf 'Use one unquoted SQL_DATABASE=DatabaseName line (letters, digits, underscore).\n' >&2
    exit 1
fi
"${compose[@]}" exec -T sqlserver bash -ceu '
    export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"
    /opt/mssql-tools18/bin/sqlcmd -C -b -S localhost -U sa -Q "SELECT 1" >/dev/null
'
printf 'Building API and portal while the current version is still running...\n'
"${compose[@]}" build api portal
printf 'Starting maintenance window: API and portal will be stopped for backup and migration.\n'
"${compose[@]}" stop portal api
backup="before-whatsapp-$(date -u +%Y%m%dT%H%M%SZ).bak"
"${compose[@]}" exec -T -e DEPLOY_DB="$database" -e DEPLOY_BACKUP="$backup" sqlserver bash -ceu '
    export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"
    /opt/mssql-tools18/bin/sqlcmd -C -b -S localhost -U sa -d "$DEPLOY_DB" -Q "
        BACKUP DATABASE [$DEPLOY_DB] TO DISK = N'\''/var/opt/mssql/backup/$DEPLOY_BACKUP'\'' WITH COPY_ONLY, CHECKSUM;
        RESTORE VERIFYONLY FROM DISK = N'\''/var/opt/mssql/backup/$DEPLOY_BACKUP'\'' WITH CHECKSUM;
        IF EXISTS (SELECT 1 FROM dbo.AspNetUsers WHERE DATALENGTH(WhatsAppPhoneNumber) > 32)
            THROW 51001, '\''Legacy WhatsApp numbers exceed 16 characters. Review and normalize them before migration.'\'', 1;
    "
'
mkdir -p backups
"${compose[@]}" cp "sqlserver:/var/opt/mssql/backup/$backup" "backups/$backup"
chmod 600 "backups/$backup"
printf 'Verified database backup: %s/backups/%s (also retained in SQL backup volume).\n' "$PWD" "$backup"

# API applies EF migrations and validates mapped columns before opening its port.
"${compose[@]}" up -d --wait --wait-timeout 300
"${compose[@]}" exec -T -e DEPLOY_DB="$database" sqlserver bash -ceu '
    export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"
    exec /opt/mssql-tools18/bin/sqlcmd -C -b -S localhost -U sa -d "$DEPLOY_DB" -i /dev/stdin
' < verify-whatsapp.sql
"${compose[@]}" ps
printf 'Deployment and WhatsApp schema checks succeeded. Now test OTP, PDF receipt and webhook delivery.\n'
