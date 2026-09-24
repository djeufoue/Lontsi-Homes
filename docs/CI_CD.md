# CI/CD setup progress

## Stage 1: continuous integration

`.github/workflows/ci.yml` runs on pushes and pull requests to `master` and can
also be started manually from the GitHub Actions tab.

It restores and builds the solution in Release mode, then runs the executable
`LontsiHomes.ScheduleTests` regression suite. A failing test returns a nonzero
exit code and prevents the Docker jobs from starting. The API and Portal images
are built for `linux/amd64`, matching the existing VPS.

This stage has read-only repository permissions and does not use production
secrets, publish images, connect to the VPS, or deploy the application.

After committing and pushing the workflow, open **Actions → CI**, select the
run for that commit, and confirm all three jobs pass. If a job fails, inspect
its first failing step before proceeding to deployment setup.

## Remaining stages

1. Publish images built by CI to GitHub Container Registry and retain their
   immutable digests for deployment.
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
have been added, but their values and connectivity still require a workflow run
to verify.
