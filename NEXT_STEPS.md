# After 1.0.0

The intended feature set is delivered: arbitrary Profile Explorer, Games/recent, Friends and mini-profiles,
selected-game lazy Achievements, paginated Inventory, independent Compare/Mutual Games, Library Pulse,
live English/Ukrainian UI, visible-row previews, local .env setup and explicit filtered CSV export.
The current release is MIT-licensed and requires no app account, server or database.

See [release verification](docs/RELEASE_VERIFICATION.md), [audit fixes](docs/RELEASE_1_0_AUDIT.md)
and [CSV semantics](docs/CSV_EXPORT.md) for actual results and limitations.

## External acceptance

1. Use a fresh permitted key locally for optional credentialed [LiveChecks](tools/GamePulse.LiveChecks/README.md).
   Desktop reads a private .env; the diagnostic remains deliberately environment-only. Do not send a key,
   reuse the legacy secret, bypass privacy or deliberately provoke rate limits. Verify allowed public/private/
   partial cases, independent profiles and selected achievement apps. Generic 401/403 is not proof of privacy.
2. Complete [audible Narrator review](docs/ACCESSIBILITY_ACCEPTANCE.md) and the native Save dialog/installer
   keyboard workflow. Automated UIA and headless peers do not establish synthesized speech.
3. Inspect supported clean Windows x64 and physical multi-monitor DPI/contrast. Current checks use the
   existing Windows host plus the separately reported GitHub CI environments. Packages are unsigned;
   trusted signing requires the maintainer's certificate or distribution identity.
4. Verify positive permitted PUBG/Rust inventory contexts. Earlier source checks retained HTTP401 as
   Unavailable; positive evidence for other contexts remains dated and scoped.

## Optional future work

- Broader permitted metadata for co-op discovery; do not infer co-op from a title.
- Optional catalogue-price estimates only with region/currency/time/coverage and allowed source use.
- Local historical snapshots or personal backlog only for an explicit opt-in scenario and documented retention.
- Additional native Linux/macOS packages, signing and an updater with a separate security/release design.

These are separate enhancements and external acceptance, not fake finished screens. Preserve honest unknown/
partial states, cancellation, bounded resources, profile independence and production real-source behavior.
Keep API keys/local .env files out of Git and packages. The Python archive remains unchanged.
