# Security policy

ChannelWave is under development. No platform has a complete release security
validation. See `docs/security-audit.md` and `docs/verification.md` for current
findings, tested behavior and platform limits. Only the latest source and beta
are maintained; there is no promise of security updates for older builds.

## Reporting a vulnerability

Use this repository's **Security → Advisories → Report a vulnerability** private
reporting flow when it is enabled. Maintainers must enable GitHub private
vulnerability reporting before making the repository public. If that option is
unavailable, open a minimal issue asking for a private reporting channel; do not
include exploitation details or private data in that public issue.

Provide the affected commit/build, platform, prerequisites, impact and minimal
reproduction using synthetic data. Never attach Telegram session databases,
phone numbers, login codes, passwords, private music, browser-profile exports,
signing keys or real account traces. Coordinate disclosure after a fix is ready.
No response deadline or bug bounty is currently promised.

## Contributor and release checks

- `python scripts/security-check.py secrets` scans all reachable Git history,
  staged blobs and nonignored working files. A shallow checkout fails. Diagnostics
  contain paths, line numbers and rule names, never matched values. This is bounded
  pattern detection, not a complete credential detector or binary analysis.
- `python scripts/security-check.py dependencies` checks resolved versions in all
  tracked NuGet platform lockfiles against NuGet's current vulnerability feed,
  then audits root npm tooling and `vendor/tdweb-build` separately. High/critical
  findings and unavailable/invalid feeds fail the gate; lower findings are
  reported for review. No automatic audit suppression is configured.
- `python scripts/security-check.py artifacts PATH` inspects output directories,
  ZIP/APK/IPA and tar archives without extraction. It rejects recognized session,
  database and signing material, path escapes, secret patterns and browser local
  configuration. Native `telegram.json` may contain only application `apiId` and
  `apiHash`. Browser inclusion requires `--allow-web-config` and the same exact
  two-field schema. These application registration values are publicly inspectable
  in distributed clients; they do not authorize a Telegram account.

Artifact checks have explicit size/depth budgets and fail when exceeded. They do
not prove the absence of credentials embedded in native binaries, opaque disk
images or encrypted archives. Inspect the unpacked application before packaging
such formats. Existing exported media outside the app is not erased by sign-out.

The security workflow runs without signing/account secrets and is included in
the required release check. A green gate does not replace authenticated Telegram,
native vault/backup, physical-device, browser or deployment validation.
