# Security policy

The current release line is **1.0.x**. Use the latest published patch and review its release notes.
This project has no guaranteed security response time or commercial support agreement.

## Report a vulnerability

Use GitHub's **Report a vulnerability** action on this repository if private reporting is available.
Otherwise open an issue requesting a private reporting channel, without exploit details or sensitive
attachments. Do not disclose a credential, credential-bearing URL or private profile payload publicly.

Include the affected version, impact, a minimal reproduction with synthetic values and any relevant
platform information. Please allow the maintainer to assess the report before publishing details.

## Protect your local key

- Keep your own permitted Steam key only in the local `.env` or process environment. `.env` is plain
  text; restrict access to your user account and never upload it.
- Leave `.env.example` empty. Do not force-add ignored local files to Git or include them in manually
  created archives, screenshots, support bundles or CI logs.
- Revoke and replace any exposed key. Removing a file or Git commit does not revoke the credential.
- Do not enable credentialed HTTP tracing/proxies or send Steam passwords/cookies to this app.

The application uses real Steam HTTPS sources, bounded transient retries and explicit availability
states. It does not bypass privacy or embed an author credential. Data caches and search recents are
in memory; CSV export writes only after an explicit user action and may contain public profile facts.
Review those files before sharing them.

See the detailed [security and privacy model](docs/SECURITY.md),
[source limitations](docs/LIVE_SOURCE_VERIFICATION.md) and
[release verification](docs/RELEASE_VERIFICATION.md) for implementation details and checked boundaries.
