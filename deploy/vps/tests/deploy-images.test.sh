#!/usr/bin/env bash
# Exercise deployment ordering and failure handling with Docker/SSH-independent
# fixtures. Real Compose merging and live application health need separate checks.
set -Eeuo pipefail
source_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
test_root=$(mktemp -d)
test_root=$(cd "$test_root" && pwd -P)
mkdir "$test_root/bin"
cat > "$test_root/bin/flock" <<'MOCK'
#!/usr/bin/env bash
exit 0
MOCK
cat > "$test_root/bin/docker" <<'MOCK'
#!/usr/bin/env bash
set -Eeuo pipefail
printf '%s\n' "$*" >> "$TEST_STATE/calls"
case "$1" in
  ps)
    last="${*: -1}"; service="${last##*=}"; printf '%s-id\n' "$service" ;;
  inspect)
    format="$3"; id="$4"; service="${id%-id}"
    case "$format" in
      *'.Mounts'*)
        case "$service" in
          sqlserver) printf 'vps_sqlserver_data /var/opt/mssql\nvps_sqlserver_backup /var/opt/mssql/backup\n' ;;
          caddy) printf 'vps_caddy_data /data\nvps_caddy_config /config\n' ;;
          *)
            prefix=vps
            if [[ "$SCENARIO" == wrong-volume ]]; then prefix=wrong; fi
            printf '%s_%s_keys /var/aspnet/data-protection-keys\n' "$prefix" "$service" ;;
        esac ;;
      '{{.State.Health.Status}}') echo healthy ;;
      '{{.Image}}') printf 'sha256:%s-new\n' "$service" ;;
      *) printf 'previous image IDs\n' ;;
    esac ;;
  compose)
    if [[ " $* " == *' config '* ]]; then
      if [[ "$SCENARIO" == config-failure ]]; then exit 12; fi
      if [[ " $* " == *' --format json '* ]]; then
        printf '{"services":{"api":{"image":"%s","container_name":"renthub-api","volumes":[{"target":"/var/aspnet/data-protection-keys","type":"volume","source":"api_keys"}]},"portal":{"image":"%s","container_name":"renthub-portal","volumes":[{"target":"/var/aspnet/data-protection-keys","type":"volume","source":"portal_keys"}]}},"volumes":{"api_keys":{"name":"vps_api_keys"},"portal_keys":{"name":"vps_portal_keys"},"sqlserver_data":{"name":"vps_sqlserver_data"}}}\n' "$API_IMAGE" "$PORTAL_IMAGE"
      fi
    elif [[ " $* " == *' up '* ]]; then
      [[ " $* " == *' --no-deps '* && " $* " == *' --no-build '* && " $* " == *' --pull never '* ]]
      [[ "${*: -2}" == 'api portal' ]]
      [[ "$SCENARIO" != startup-failure ]] || exit 13
    fi ;;
  login)
    cat > /dev/null
    printf '%s\n' "$DOCKER_CONFIG" > "$TEST_STATE/auth-path"
    printf '{}\n' > "$DOCKER_CONFIG/config.json" ;;
  pull) [[ "$SCENARIO" != pull-failure ]] || exit 14 ;;
  image)
    if [[ "$4" == '{{.Os}}/{{.Architecture}}' ]]; then echo linux/amd64
    elif [[ "$5" == "$API_IMAGE" ]]; then echo sha256:api-new
    else echo sha256:portal-new; fi ;;
  stop) [[ "${*: -2}" == 'portal-id api-id' ]] ;;
  start) [[ "$*" == 'start api-id portal-id' ]] ;;
  exec)
    if [[ " $* " == *' sha256sum '* ]]; then
      printf backup | sha256sum
    else
      cat > "$TEST_STATE/sql-input"
      if grep -q 'BACKUP DATABASE' "$TEST_STATE/sql-input"; then
        [[ "$SCENARIO" != backup-failure ]] || exit 15
      elif [[ "$SCENARIO" == schema-failure ]]; then exit 16; fi
    fi ;;
  cp) printf backup > "$3" ;;
  *) echo "Unexpected Docker operation: $1" >&2; exit 99 ;;
esac
MOCK
chmod +x "$test_root/bin/"*
export PATH="$test_root/bin:$PATH"
export API_IMAGE="ghcr.io/djeufoue/lontsi-homes-api@sha256:$(printf '%064d' 0)"
export PORTAL_IMAGE="ghcr.io/djeufoue/lontsi-homes-portal@sha256:$(printf '%064d' 1)"
export SOURCE_SHA=$(printf '%040d' 0)
export REGISTRY_USER=mock-user REGISTRY_TOKEN=mock-token

for scenario in success config-failure wrong-volume pull-failure backup-failure startup-failure schema-failure; do
    export SCENARIO="$scenario" TEST_STATE="$test_root/$scenario-state"
    mkdir "$TEST_STATE"
    root="$test_root/$scenario"
    release="$root/.ci-releases/123-1"
    mkdir -p "$release"
    printf 'SQL_DATABASE=TestDb\n' > "$root/.env.production"
    printf 'services: {}\n' > "$root/docker-compose.prod.yml"
    cp "$source_dir/compose.images.yml" "$source_dir/verify-whatsapp.sql" "$source_dir/verify-payment-corrections.sql" "$release/"
    # Only replace the installation root in this isolated fixture.
    sed "s|/opt/renthub/deploy/vps|$root|g" "$source_dir/deploy-images.sh" > "$release/deploy-images.sh"
    result=0
    bash "$release/deploy-images.sh" > "$TEST_STATE/output" 2>&1 || result=$?
    if [[ "$scenario" == success ]]; then
        [[ "$result" == 0 ]] || { cat "$TEST_STATE/output"; exit 1; }
        [[ "$(cat "$root/current-ci-release")" == "$release" ]]
        test -s "$(cat "$release/backup-path.txt")"
        ! grep -q '^start ' "$TEST_STATE/calls"
    else
        [[ "$result" != 0 ]]
        test ! -e "$root/current-ci-release"
        case "$scenario" in
          config-failure|wrong-volume|pull-failure)
            ! grep -q '^stop ' "$TEST_STATE/calls"
            ! grep -q ' up ' "$TEST_STATE/calls" ;;
          backup-failure)
            grep -q '^start api-id portal-id$' "$TEST_STATE/calls"
            ! grep -q ' up ' "$TEST_STATE/calls" ;;
          startup-failure|schema-failure)
            ! grep -q '^start ' "$TEST_STATE/calls"
            grep -q 'Automatic rollback is disabled' "$TEST_STATE/output" ;;
        esac
    fi
    if [[ -f "$TEST_STATE/auth-path" ]]; then test ! -e "$(cat "$TEST_STATE/auth-path")"; fi
    ! grep -q 'mock-token' "$TEST_STATE/output"
    ! grep -Eq '^compose .* (down|build)|^volume (rm|prune)|^system prune' "$TEST_STATE/calls"
    printf 'PASS deployment scenario: %s\n' "$scenario"
done
