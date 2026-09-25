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

### Deployment work remaining

1. Run the image access check to verify private registry access from the VPS.
2. Add a deployment job gated by the `production` environment approval.
3. Connect using `VPS_SSH_KEY` with strict host verification against
   `VPS_SSH_KNOWN_HOSTS`.
4. Preserve `/opt/renthub/deploy/vps`, Compose project `vps`, the existing
   production environment file, and database volumes. The server checkout was
   reported on an older Git history; reconcile it explicitly before automated
   updates instead of assuming a fast-forward pull will work.
5. Back up and verify the database, deploy the exact published image digests,
   and run readiness and schema checks before reporting success.

Configured production environment variables: `VPS_HOST`, `VPS_USER`,
`VPS_DEPLOY_PATH`, and `COMPOSE_PROJECT_NAME`. The two SSH environment secrets
have been verified by the successful production connection check.
