# Security and configuration

## Credentials

- Supply credentials through deployment environment variables, a secret store, or ignored
  local configuration. The tracked `appsettings.json` files must contain only safe defaults.
- Never commit `.env` files, private keys, database backups, source archives, or build output.
  `.gitignore` does not protect files already tracked or remove earlier commits.
- Administrator bootstrapping is opt-in and has no default email or password. Disable it
  after setup. Rotate an existing account password through the account-management flow;
  changing an environment variable does not change an existing account password.
- Use separate development and production credentials. Do not reuse example values.

## Before making a repository public

1. Scan the current tree and **all Git branches and tags**, including historical build output
   and archives. For example, with [Gitleaks](https://github.com/gitleaks/gitleaks):

   ```sh
   gitleaks git --redact --log-opts="--all" .
   ```

2. Revoke or rotate any real credentials found in history, even if the current files are clean.
   Coordinate changes to API signing keys, storage, SMTP, payment providers, and deployed
   environment settings before restarting production services.
3. Remove sensitive data from history on every affected branch and tag. Rewriting shared
   history changes commit IDs and requires coordinated replacement of other clones.
   Avoid merging old history back into the cleaned repository.
4. Review GitHub pull requests, issues, Actions logs/artifacts, releases, and any other hosted
   copies. They are not covered by a local source scan. Enable secret scanning and push
   protection where available. A scanner cannot guarantee the absence of all sensitive data.

Follow GitHub's [sensitive-data removal guide](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/removing-sensitive-data-from-a-repository).

## Reporting a vulnerability

Do not include credentials or personal data in a public issue. Use GitHub private vulnerability
reporting if enabled, or contact the repository owner privately.
