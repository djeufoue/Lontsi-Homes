#!/usr/bin/env bash
# Validate registry access only. Never pull layers or change running containers.
set -Eeuo pipefail
umask 077

[[ "${VPS_HOST:-}" == '51.255.193.136' ]]
[[ "${VPS_USER:-}" == 'ubuntu' ]]
[[ -n "${GH_TOKEN:-}" && -n "${GITHUB_ACTOR:-}" ]]
[[ -n "${VPS_SSH_KEY:-}" && -n "${VPS_SSH_KNOWN_HOSTS:-}" ]]

image_prefix="ghcr.io/${GITHUB_REPOSITORY,,}"
api_image=$(cat "$IMAGE_REFERENCE_DIR/api.txt")
portal_image=$(cat "$IMAGE_REFERENCE_DIR/portal.txt")
validate_reference() {
    local component="$1" reference="$2" digest
    [[ "$reference" == "$image_prefix-$component@sha256:"* ]] || return 1
    digest=${reference#"$image_prefix-$component@sha256:"}
    [[ "$digest" =~ ^[a-f0-9]{64}$ ]]
}
validate_reference api "$api_image" || { echo 'Invalid API image digest reference.' >&2; exit 1; }
validate_reference portal "$portal_image" || { echo 'Invalid Portal image digest reference.' >&2; exit 1; }

ssh_dir=$(mktemp -d "$RUNNER_TEMP/registry-ssh.XXXXXX")
trap 'rm -rf -- "$ssh_dir"' EXIT
printf '%s\n' "$VPS_SSH_KEY" | tr -d '\r' > "$ssh_dir/key"
printf '%s\n' "$VPS_SSH_KNOWN_HOSTS" | tr -d '\r' > "$ssh_dir/known_hosts"
unset VPS_SSH_KEY VPS_SSH_KNOWN_HOSTS
ssh-keygen -y -P '' -f "$ssh_dir/key" > /dev/null
ssh-keygen -F "$VPS_HOST" -f "$ssh_dir/known_hosts" > /dev/null

# Send shell-escaped inputs over encrypted stdin; the token is never a command
# argument, artifact, summary value, or permanent VPS Docker credential.
{
    printf 'set -Eeuo pipefail\n'
    printf 'registry_user=%q\n' "$GITHUB_ACTOR"
    printf 'registry_token=%q\n' "$GH_TOKEN"
    printf 'api_image=%q\n' "$api_image"
    printf 'portal_image=%q\n' "$portal_image"
    cat <<'REMOTE'
umask 077
registry_dir=$(mktemp -d /tmp/lontsihomes-registry-check.XXXXXX)
trap 'rm -rf -- "$registry_dir"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM HUP
printf '%s' "$registry_token" | timeout 60s docker --config "$registry_dir" login ghcr.io --username "$registry_user" --password-stdin
unset registry_token
for image in "$api_image" "$portal_image"; do
    timeout 60s docker --config "$registry_dir" manifest inspect "$image" > /dev/null
    printf 'Registry manifest accessible: %s\n' "$image"
done
printf 'Both image manifests are accessible. No image layers were pulled and no containers were changed.\n'
REMOTE
} | ssh -T -i "$ssh_dir/key" \
    -o BatchMode=yes \
    -o IdentitiesOnly=yes \
    -o StrictHostKeyChecking=yes \
    -o "UserKnownHostsFile=$ssh_dir/known_hosts" \
    -o GlobalKnownHostsFile=/dev/null \
    -o HostKeyAlgorithms=ssh-ed25519 \
    -o UpdateHostKeys=no \
    -o ConnectTimeout=15 \
    -o ServerAliveInterval=15 \
    -o ServerAliveCountMax=3 \
    "$VPS_USER@$VPS_HOST" 'bash -se'

{
    printf '\n### VPS registry access passed\n\n'
    printf 'API: `%s`\n\nPortal: `%s`\n\n' "$api_image" "$portal_image"
    printf 'Manifest access verified using temporary credentials. Image-layer downloads, application startup, and database migrations have not been tested by this check.\n'
} >> "$GITHUB_STEP_SUMMARY"
