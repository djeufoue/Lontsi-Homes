# CI/CD setup progress

## Stage 1: continuous integration

`.github/workflows/ci.yml` runs on pushes and pull requests to `master` and can
also be started manually from the GitHub Actions tab.

It restores and builds the solution in Release mode, then runs the executable
`LontsiHomes.ScheduleTests` regression suite. A failing test returns a nonzero
exit code and prevents the Docker jobs from starting. The API and Portal images
are built for `linux/amd64`, matching the existing VPS.

The build-and-test job and pull-request Docker jobs have read-only repository
permissions. The first CI run for commit `f360b50` passed all three jobs on GitHub.

After committing and pushing the workflow, open **Actions → CI**, select the
run for that commit, and confirm the build-and-test job and both image jobs pass.
If a job fails, inspect its first failing step before proceeding.

## Stage 2: publish Docker images

After tests pass on a push to `master` (or a manual run on `master`), the two
`publish-images` jobs build and push the images to:

- `ghcr.io/djeufoue/lontsi-homes-api`
- `ghcr.io/djeufoue/lontsi-homes-portal`

The workflow derives these names from the repository name in lowercase. Each
image has a tag containing the full source commit, workflow run ID, and attempt.
Its immutable `name@sha256:...` reference appears in the job summary and is saved
as an `image-reference-<component>-<attempt>` artifact for 30 days. Future
deployment must use both digest references from the same successful run; a
single published image is not a complete release. Artifacts contain references,
not the images themselves. The images are stored in the registry.

Only the publishing jobs receive `packages: write`. They authenticate using
GitHub's automatically provided `GITHUB_TOKEN`; no new personal access token or
production secret is needed for publishing. Pull requests and manual runs on
other branches run the original build-only Docker jobs, without registry login.
On `master`, those build-only jobs are skipped and the publishing jobs build the
images instead. There is no VPS connection or deployment in this stage.

After pushing this update, check that **Build and regression tests**, **Publish
api image**, and **Publish portal image** pass. Check the summary for both digest
references and the repository's Packages area for the two linked packages.
New GHCR packages default to private; repository visibility does not make them
public automatically. Keep the defaults for now. Registry pull authentication
for the VPS will be configured in the deployment stage.

If publication fails with a permission error, check the failed step first. An
existing package with the same name may need to grant this repository Actions
access in its package settings. Do not broaden repository-wide token permissions
or make a package public just to bypass an error.

References: [GitHub publishing guide](https://docs.github.com/en/actions/tutorials/publish-packages/publish-docker-images)
and [Container registry access](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry).

## Remaining stages

The first image-publishing run (`36076166049`) succeeded. The user also verified
that the running Compose project is `vps`, with these volume mounts:

| Service | Existing volume | Container destination |
| --- | --- | --- |
| sqlserver | vps_sqlserver_data | /var/opt/mssql |
| sqlserver | vps_sqlserver_backup | /var/opt/mssql/backup |
| api | vps_api_keys | /var/aspnet/data-protection-keys |
| portal | vps_portal_keys | /var/aspnet/data-protection-keys |
| caddy | vps_caddy_data | /data |
| caddy | vps_caddy_config | /config |

Caddy also binds `/opt/renthub/deploy/vps/Caddyfile` to
`/etc/caddy/Caddyfile`. Containers still have the `renthub-` name prefix.

### Manual production connection check

`.github/workflows/production-check.yml` only runs through **Actions → Production
connection check → Run workflow**. Select `master`, then approve the `production`
environment request using **Review deployments**. Approval permits this check to
use the SSH secrets; this workflow does not deploy or restart the application.

It verifies the saved private key and pinned host key, logs in as `ubuntu`, checks
Docker access, confirms production files are readable and the deployment folder
is writable, and asserts that each running service uses the expected existing
volume mounts. It reports the current Git revision and tracked changes without
updating the server checkout. It does not print environment-file contents, pull
images, change containers, or modify database data. Temporary SSH files exist
only on the GitHub runner and are removed when the step ends. The four target
settings are checked against the installation verified during setup; changing
the VPS requires deliberately updating these checks as well as GitHub variables.

The job is skipped if dispatched from a branch other than `master`. A successful
check confirms connectivity and mounts, not registry pull access, backup health,
or readiness to deploy the renamed application.

### Verified setup

The production connection check passed in run `36077411497`, including the
approval gate, SSH host verification, Docker access, and all six volume mounts.
The production variable-name inventory was reviewed. The user has no Google
Geocoding key and has not edited `.env.production`; geocoding remains disabled.
`ADMIN_SEED_ENABLED` and the WhatsApp URL-button variables also remain absent;
their Compose defaults apply. Variable names alone do not validate secret values
or whether required settings are nonempty.

### Manual production image access check

Push `.github/workflows/image-access-check.yml` and its helper script, then open
**Actions → Production image access check → Run workflow**, select `master`, and
approve the `production` environment request. Leave `ci_run_id` empty to select
the latest successful CI push on `master`, or enter a specific successful CI run
ID. This check is independent of the automatic CI run caused by pushing it.

It verifies the selected run's repository, workflow, branch, event, and success
status. It downloads only that run attempt's two image-reference artifacts and
accepts only the expected repository's API and Portal SHA-256 digest references.
Expired or missing artifacts make the check fail instead of falling back to tags.

The VPS logs into GHCR using the check job's temporary `GITHUB_TOKEN`, which has
read-only package access. Credentials travel over verified SSH and are stored
in an isolated mode-700 temporary Docker configuration directory, removed when
the remote script exits normally or handles termination. Existing Docker login
settings are untouched. No personal access token needs to be created.

The check reads both image manifests from the VPS. It does not pull image layers,
run images, update the server checkout or production environment file, restart
services, or modify the database. Its summary identifies exactly which commit
and image digests were checked. Success proves registry metadata access; the
real deployment must still verify downloads, backups, and application readiness.

### Verified registry and backup preparation

Image access check `36078966696` succeeded for CI run `36078326537`. An online
copy-only backup of `RentHubDb` also passed `RESTORE VERIFYONLY WITH CHECKSUM`
and was copied into `/opt/renthub/deploy/vps/backups/` (approximately 39.7 MB).
These checks did not deploy the application. Backup verification checks
readability/completeness and checksums; it is not a full restore rehearsal.

## Stage 3: manual production deployment

**`Deploy production` changes the live API and Portal and may apply database
migrations. It includes a maintenance window.** Pushing its workflow only runs
CI; deployment is deliberately manual for this rollout.

1. Commit and push the deployment files to `master`.
2. Wait for that exact commit's CI run to finish successfully. Copy its numeric
   run ID from the URL (`.../actions/runs/<ID>`).
3. Open **Actions → Deploy production → Run workflow**, select `master`, and
   enter that CI run ID.
4. Review the intended commit and approve the `production` environment request
   when ready for the maintenance window. This approval authorizes real deployment.
5. Verify the deployment job succeeds, then check public login, a property page,
   and a rent receipt. Check messaging only for approved/configured templates.

The selected CI run must be successful, belong to this repository and `ci.yml`,
and match the deployment workflow's exact `master` commit. The runner checks
the current master ref again before contacting the VPS. If master has advanced,
run CI for the new commit and start a new deployment instead of using stale inputs.
Both image references come from the same run attempt; expired artifacts or a
partial rerun lacking either reference fail closed.

### What deployment does

- Transfers only the deployment script, Compose overlay, and two schema checks
  into `/opt/renthub/deploy/vps/.ci-releases/<deployment-run>-<attempt>/`.
- Holds an exclusive VPS deployment lock, checks running services and all six
  existing mounts, and validates the resolved application configuration.
- Snapshots the server's existing Compose file as `base-compose.yml` in the
  release directory. It overlays the exact image digests and updated optional
  application settings, removes source builds, and disables initial admin seeding.
- Uses a temporary read-only GitHub token to download both images, verifies their
  Linux/amd64 architecture, and records the previous application image IDs.
- Stops only the API and Portal, makes a fresh SQL backup, verifies it, copies it
  to the host backup directory, and compares checksums of the two copies.
- Starts only API and Portal using `--no-deps --no-build --pull never`. SQL Server
  and Caddy are not recreated. The API applies migrations before opening its port.
- Waits for API readiness and a successful Portal HTTP response, runs the
  WhatsApp/payment schema checks, and verifies image IDs and preserved key mounts.
- Records the successful release path in `current-ci-release` and removes the
  temporary registry credentials. Release files and backups remain on the VPS.

The older server Git history is left intact. There is no `git reset`, source
checkout update, or replacement of `.env.production`, the Caddyfile, network
configuration, container names, or database volumes. This avoids combining a
history migration with the first automated application deployment. The overlay
expects the confirmed legacy `renthub-api` and `renthub-portal` container names.

### Failure handling and operating the deployed version

Failures before maintenance leave existing services running. If backup or its
host copy fails after stopping the application, the script attempts to restart
the previous containers. Once the new version may have started, the script does
not automatically switch images back: migrations may already have changed the
database. Inspect the job error and preserve the verified backup before recovery.
The summary is written only after every deployment check passes.

`backup-path.txt`, `source-commit.txt`, and `previous-images.txt` in the release
directory identify the backup, deployed source, and previous application images.
For diagnostics, use the release directory printed in the job log, including
after a failed deployment. For example, replace `<release-directory>` below:

```bash
cd /opt/renthub/deploy/vps
docker compose -p vps --project-directory "$PWD" --env-file "$PWD/.env.production" \
  -f '<release-directory>/base-compose.yml' -f '<release-directory>/images.yml' ps
```

Use the same files with `logs --tail 100 api portal` to investigate startup
failures. Future updates should use `Deploy production`; the old server checkout
and its build-based `deploy-update.sh` still refer to old source code. Do not use
that older script or a bare Compose `up --build` to update a CI-deployed release.
Keep the active release files because Compose uses their paths for diagnostics.
Future changes to runtime configuration must be included in the image overlay
or handled explicitly on the server; updating repository Compose alone does not
update the retained VPS base configuration.

### Validation limits

`deploy/vps/tests/deploy-images.test.sh` runs in CI and exercises successful
deployment, invalid configuration, wrong volumes, failed pulls, failed backups,
startup failures, and schema failures using a Docker stub. These check ordering,
cleanup, and restart/rollback behavior without touching production. Workflow and
shell syntax plus real Compose merge validation are also checked locally.
Live image startup and migration behavior remain unverified until the first
approved production deployment. Database backups retained here are on the same
VPS; off-server backup storage remains separate work.

### First deployment health-check correction

The first deployment (`36080423569`, source `427ea0e`) downloaded both images,
verified a fresh backup, and recreated the application containers. The API was
healthy and the Portal logs showed successful startup on port 8080. The Portal
health probe failed with Bash `unexpected EOF while looking for ']]'` from the
probe's shell expression, so the workflow failed before schema verification and
before writing `current-ci-release`. This was not a pre-deployment failure.

The corrected probe uses exec-form `CMD`, passes the script directly to Bash,
and checks the HTTP status line with `grep` instead of the malformed Bash
expression. CI now renders the actual Compose health-check configuration and
executes it against controlled HTTP responses, including successful responses,
redirects, HTTP errors, and a closed port. These tests require no Docker daemon.

To apply the fix, push it, wait for the new CI run, then manually deploy that
new run ID. The workflow takes another verified backup and recreates the Portal
with the corrected health-check configuration. Do not reuse the old CI run ID
or treat the first deployment as rolled back. Review schema checks and public
application behavior after the corrected deployment succeeds.

Configured production environment variables: `VPS_HOST`, `VPS_USER`,
`VPS_DEPLOY_PATH`, and `COMPOSE_PROJECT_NAME`. The two SSH environment secrets
have been verified by the successful production connection check.
