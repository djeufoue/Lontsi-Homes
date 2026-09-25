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

1. Verify the first publishing run and configure authenticated registry pulls.
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
