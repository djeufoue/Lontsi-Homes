#!/usr/bin/env bash
# Run only through the approved deployment workflow on the existing VPS.
set -Eeuo pipefail
umask 077
release_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
[[ "$release_dir" =~ ^/opt/renthub/deploy/vps/\.ci-releases/[0-9]+-[0-9]+$ ]]
cd /opt/renthub/deploy/vps
[[ "$(pwd -P)" == /opt/renthub/deploy/vps ]]
[[ "$API_IMAGE" =~ ^ghcr\.io/djeufoue/lontsi-homes-api@sha256:[a-f0-9]{64}$ ]]
[[ "$PORTAL_IMAGE" =~ ^ghcr\.io/djeufoue/lontsi-homes-portal@sha256:[a-f0-9]{64}$ ]]
[[ "$SOURCE_SHA" =~ ^[a-f0-9]{40}$ ]]
test -r .env.production
test -r docker-compose.prod.yml
command -v python3 > /dev/null
command -v flock > /dev/null
exec 9>.ci-deploy.lock
flock -n 9 || { echo 'Another deployment holds the VPS lock.' >&2; exit 1; }

registry_dir=''
stopped=0
new_started=0
cleanup() {
    result=$?
    trap - EXIT
    if (( result != 0 )); then
        printf 'Deployment failed. Release files: %s\n' "$release_dir" >&2
        if (( stopped == 1 && new_started == 0 )); then
            echo 'No new application was started; restarting the previous containers.' >&2
            docker start "$api_before" "$portal_before" || true
        elif (( new_started == 1 )); then
            echo 'Migrations may have run. Automatic rollback is disabled; inspect the failure and retain the backup.' >&2
        fi
    fi
    if [[ -n "$registry_dir" ]]; then rm -rf -- "$registry_dir"; fi
    exit "$result"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM HUP

container_for() {
    local ids
    ids=$(docker ps -q --filter label=com.docker.compose.project=vps --filter "label=com.docker.compose.service=$1")
    [[ -n "$ids" && "$ids" != *$'\n'* ]] || { echo "Expected one running $1 container." >&2; return 1; }
    printf '%s' "$ids"
}
sql_before=$(container_for sqlserver)
caddy_before=$(container_for caddy)
api_before=$(container_for api)
portal_before=$(container_for portal)
[[ "$(docker inspect --format '{{.State.Health.Status}}' "$sql_before")" == healthy ]]
check_mount() {
    local mounts
    mounts=$(docker inspect --format '{{range .Mounts}}{{println .Name .Destination}}{{end}}' "$1")
    grep -Fxq -- "$2 $3" <<< "$mounts"
}
check_mount "$sql_before" vps_sqlserver_data /var/opt/mssql
check_mount "$sql_before" vps_sqlserver_backup /var/opt/mssql/backup
check_mount "$api_before" vps_api_keys /var/aspnet/data-protection-keys
check_mount "$portal_before" vps_portal_keys /var/aspnet/data-protection-keys
check_mount "$caddy_before" vps_caddy_data /data
check_mount "$caddy_before" vps_caddy_config /config

database=$(sed -n 's/^SQL_DATABASE=//p' .env.production | tr -d '\r')
[[ "$database" =~ ^[A-Za-z0-9_]+$ ]] || { echo 'Expected one unquoted SQL_DATABASE name.' >&2; exit 1; }
sed -e "s|__API_IMAGE__|$API_IMAGE|g" -e "s|__PORTAL_IMAGE__|$PORTAL_IMAGE|g" \
    "$release_dir/compose.images.yml" > "$release_dir/images.yml"
cp -- docker-compose.prod.yml "$release_dir/base-compose.yml"
compose=(docker compose --project-name vps --project-directory "$PWD" --env-file "$PWD/.env.production" -f "$release_dir/base-compose.yml" -f "$release_dir/images.yml")
"${compose[@]}" config --quiet
# Inspect resolved configuration through a pipe; never print or persist secrets.
"${compose[@]}" config --format json | python3 -c '
import json, os, sys
c = json.load(sys.stdin)
for service, var in (("api", "API_IMAGE"), ("portal", "PORTAL_IMAGE")):
    s = c["services"][service]
    assert s["image"] == os.environ[var], "Unexpected image"
    assert not s.get("build"), "Build must be disabled"
    assert s.get("container_name") == "renthub-" + service, "Unexpected container name"
    target = "/var/aspnet/data-protection-keys"
    mounts = [v for v in s["volumes"] if v["target"] == target]
    assert len(mounts) == 1 and mounts[0]["type"] == "volume", "Invalid key mount"
    assert c["volumes"][mounts[0]["source"]]["name"] == "vps_" + service + "_keys", "Wrong key volume"
assert c["volumes"]["sqlserver_data"]["name"] == "vps_sqlserver_data", "Wrong database volume"
'

registry_dir=$(mktemp -d /tmp/lontsihomes-deploy-auth.XXXXXX)
export DOCKER_CONFIG="$registry_dir"
printf '%s' "$REGISTRY_TOKEN" | docker login ghcr.io --username "$REGISTRY_USER" --password-stdin
unset REGISTRY_TOKEN
# Download both exact images before starting any maintenance window.
docker pull "$API_IMAGE"
docker pull "$PORTAL_IMAGE"
for image in "$API_IMAGE" "$PORTAL_IMAGE"; do
    [[ "$(docker image inspect --format '{{.Os}}/{{.Architecture}}' "$image")" == linux/amd64 ]]
done
docker inspect --format '{{.Name}} {{.Image}} {{.Config.Image}}' "$api_before" "$portal_before" > "$release_dir/previous-images.txt"
printf '%s\n' "$SOURCE_SHA" > "$release_dir/source-commit.txt"

mkdir -p backups
backup="before-ci-${release_dir##*/}-$(date -u +%Y%m%dT%H%M%S%N).bak"
printf '%s\n' "$PWD/backups/$backup" > "$release_dir/backup-path.txt"
echo 'Starting maintenance window: stopping Portal and API for backup and migrations.'
stopped=1
docker stop --time 30 "$portal_before" "$api_before"
docker exec -i -e DEPLOY_DB="$database" -e DEPLOY_BACKUP="$backup" "$sql_before" bash -se <<'SQL'
set -Eeuo pipefail
export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"
/opt/mssql-tools18/bin/sqlcmd -C -b -S localhost -U sa -d "$DEPLOY_DB" -Q "
BACKUP DATABASE [$DEPLOY_DB] TO DISK = N'/var/opt/mssql/backup/$DEPLOY_BACKUP' WITH COPY_ONLY, CHECKSUM;
RESTORE VERIFYONLY FROM DISK = N'/var/opt/mssql/backup/$DEPLOY_BACKUP' WITH CHECKSUM;
IF EXISTS (SELECT 1 FROM dbo.AspNetUsers WHERE DATALENGTH(WhatsAppPhoneNumber) > 32)
    THROW 51001, 'Legacy WhatsApp numbers exceed 16 characters. Review before migration.', 1;"
SQL
docker cp "$sql_before:/var/opt/mssql/backup/$backup" "backups/$backup"
chmod 600 "backups/$backup"
remote_hash=$(docker exec "$sql_before" sha256sum "/var/opt/mssql/backup/$backup")
local_hash=$(sha256sum "backups/$backup")
[[ "${remote_hash%% *}" == "${local_hash%% *}" ]]
printf 'Verified backup and copied file: %s/backups/%s\n' "$PWD" "$backup"

# From this point forward, startup may migrate the DB. Do not auto-revert images.
new_started=1
"${compose[@]}" up -d --no-deps --no-build --pull never --wait --wait-timeout 300 api portal
for schema in verify-whatsapp.sql verify-payment-corrections.sql; do
    docker exec -i -e DEPLOY_DB="$database" "$sql_before" bash -ceu '
      export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"
      exec /opt/mssql-tools18/bin/sqlcmd -C -b -S localhost -U sa -d "$DEPLOY_DB" -i /dev/stdin
    ' < "$release_dir/$schema"
done
[[ "$(container_for sqlserver)" == "$sql_before" ]]
[[ "$(container_for caddy)" == "$caddy_before" ]]
for service in api portal; do
    id=$(container_for "$service")
    [[ "$(docker inspect --format '{{.State.Health.Status}}' "$id")" == healthy ]]
    expected="$API_IMAGE"
    if [[ "$service" == portal ]]; then expected="$PORTAL_IMAGE"; fi
    [[ "$(docker inspect --format '{{.Image}}' "$id")" == "$(docker image inspect --format '{{.Id}}' "$expected")" ]]
    check_mount "$id" "vps_${service}_keys" /var/aspnet/data-protection-keys
done
printf '%s\n' "$release_dir" > .current-ci-release.tmp
mv -- .current-ci-release.tmp current-ci-release
"${compose[@]}" ps
printf 'Deployment succeeded for commit %s. Backup: %s/backups/%s\n' "$SOURCE_SHA" "$PWD" "$backup"
