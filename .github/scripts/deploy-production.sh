#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
[[ "$VPS_HOST" == 51.255.193.136 && "$VPS_USER" == ubuntu ]]
[[ "$VPS_DEPLOY_PATH" == /opt/renthub/deploy/vps && "$COMPOSE_PROJECT_NAME" == vps ]]
[[ "$GITHUB_RUN_ID" =~ ^[0-9]+$ && "$GITHUB_RUN_ATTEMPT" =~ ^[0-9]+$ ]]
[[ "$GITHUB_SHA" =~ ^[a-f0-9]{40}$ ]]
api_image=$(cat "$IMAGE_REFERENCE_DIR/api.txt")
portal_image=$(cat "$IMAGE_REFERENCE_DIR/portal.txt")
[[ "$api_image" =~ ^ghcr\.io/djeufoue/lontsi-homes-api@sha256:[a-f0-9]{64}$ ]]
[[ "$portal_image" =~ ^ghcr\.io/djeufoue/lontsi-homes-portal@sha256:[a-f0-9]{64}$ ]]
[[ -n "$GH_TOKEN" && -n "$VPS_SSH_KEY" && -n "$VPS_SSH_KNOWN_HOSTS" ]]
# Recheck after environment approval and artifact download to reject stale runs.
current_sha=$(gh api "repos/$GITHUB_REPOSITORY/git/ref/heads/master" --jq '.object.sha')
[[ "$current_sha" == "$GITHUB_SHA" ]] || { echo 'master changed. Select its successful CI run in a new deployment.' >&2; exit 1; }

work=$(mktemp -d "$RUNNER_TEMP/deploy-production.XXXXXX")
trap 'rm -rf -- "$work"' EXIT
printf '%s\n' "$VPS_SSH_KEY" | tr -d '\r' > "$work/key"
printf '%s\n' "$VPS_SSH_KNOWN_HOSTS" | tr -d '\r' > "$work/known_hosts"
unset VPS_SSH_KEY VPS_SSH_KNOWN_HOSTS
ssh-keygen -y -P '' -f "$work/key" > /dev/null
ssh-keygen -F "$VPS_HOST" -f "$work/known_hosts" > /dev/null
tar -czf "$work/release.tar.gz" -C deploy/vps \
    deploy-images.sh compose.images.yml verify-whatsapp.sql verify-payment-corrections.sql
release_id="$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT"
{
    printf 'set -Eeuo pipefail\numask 077\n'
    printf 'export API_IMAGE=%q\n' "$api_image"
    printf 'export PORTAL_IMAGE=%q\n' "$portal_image"
    printf 'export SOURCE_SHA=%q\n' "$GITHUB_SHA"
    printf 'export REGISTRY_USER=%q\n' "$GITHUB_ACTOR"
    printf 'export REGISTRY_TOKEN=%q\n' "$GH_TOKEN"
    printf 'release_id=%q\n' "$release_id"
    cat <<'REMOTE'
cd /opt/renthub/deploy/vps
[[ "$(pwd -P)" == /opt/renthub/deploy/vps ]]
mkdir -p .ci-releases
[[ ! -L .ci-releases ]]
release_dir="$PWD/.ci-releases/$release_id"
mkdir "$release_dir"
base64 --decode <<'RELEASE_ARCHIVE' | tar -xz -C "$release_dir"
REMOTE
    base64 "$work/release.tar.gz"
    printf 'RELEASE_ARCHIVE\n'
    printf 'exec bash "$release_dir/deploy-images.sh"\n'
} | ssh -T -i "$work/key" \
    -o BatchMode=yes -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes \
    -o "UserKnownHostsFile=$work/known_hosts" -o GlobalKnownHostsFile=/dev/null \
    -o HostKeyAlgorithms=ssh-ed25519 -o UpdateHostKeys=no \
    -o ConnectTimeout=15 -o ServerAliveInterval=15 -o ServerAliveCountMax=3 \
    "$VPS_USER@$VPS_HOST" 'bash -se'

{
    printf '### Production deployment succeeded\n\nCommit: `%s`\n\n' "$GITHUB_SHA"
    printf 'API: `%s`\n\nPortal: `%s`\n\n' "$api_image" "$portal_image"
    printf 'Release files and backup record: `/opt/renthub/deploy/vps/.ci-releases/%s`\n\n' "$release_id"
    printf 'Database backup verified; application health and schema checks passed. Test public login and a rent receipt next.\n'
} >> "$GITHUB_STEP_SUMMARY"
