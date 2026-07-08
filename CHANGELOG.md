# Changelog

All notable changes to the Keylight C# SDK are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.1] — 2026-07-08

### Fixed

- **Revocation now enforced on launch.** `CheckOnLaunchAsync` always performs a server
  validate, so a dashboard revoke/expiry lands on the next launch. A real HTTP 422 revoke
  response is now decoded and reconciled (clearing the stale cached lease) instead of being
  swallowed as a transient failure, and a `Valid == false` rejection with no lease clears
  the cached lease.
- **Offline use bounded by `MaxOfflineDays`.** A signed lease can no longer outlive the
  offline cap; `ResolveState` now applies the same bound the entitlement gate already used.
- **Unity mirror brought to parity** (`dev.keylight.sdk` 0.1.1). The hand-maintained Unity
  copy still carried the pre-fix logic and would have shipped unpatched; its `MaxOfflineDays`
  default also moves 7 → 15 to match the NuGet SDK.

## [0.1.0] — 2026-06-18

Initial release of the Keylight C# SDK.

### Added

- **Offline Ed25519 lease verification** — pure-managed implementation with no native
  dependencies; IL2CPP-safe and WebGL-compatible. Reconstructs the canonical signed payload
  (entitlements sorted, pipe-delimited) and verifies against the tenant's trusted keyset with
  a 300-second clock-skew tolerance.
- **State machine** — `KeylightState` enum (`Licensed`, `Trial`, `Expired`, `Invalid`) resolved
  synchronously from the in-memory verified-lease cache, with no network call.
- **License lifecycle** — `ActivateAsync` / `ValidateAsync` / `DeactivateAsync` /
  `CheckOnLaunchAsync` / `RefreshIfNeededAsync` (debounced). Synchronous wrappers provided for
  non-async contexts.
- **Entitlements** — Feature keys embedded in the signed lease, gated offline via
  `client.HasEntitlement("feature-key")`.
- **Trials** — Auto-started local trial clock on first launch when `TrialDurationDays` is
  configured; no server round-trip required.
- **Dual async/sync API** — All network operations are `Task`-returning with `CancellationToken`
  support; synchronous wrappers use `ConfigureAwait(false)` throughout.
- **Device telemetry** — `sdk_version`, `platform`, and optional `app_version` attached on every
  activate/validate call.
- **Pluggable interfaces** — `ILeaseStore` and `IKeylightTransport` for custom storage backends
  and HTTP transports (test-friendly).
- **NuGet package** (`Keylight`) — targets `netstandard2.0` and `net8.0`; compatible with .NET
  Framework 4.6.1+, .NET Core, Mono, and Godot 4 via NuGet.
- **Unity UPM package** (`dev.keylight.sdk`) — installable via UPM or OpenUPM; source synced
  from the core library via `unity/sync-core.sh`.
- **Cross-SDK conformance parity** — all 8 frozen conformance vectors pass, keeping offline
  verification behavior identical to the Swift, Rust, and JavaScript SDKs.
- **Nullable annotations** throughout; `.snupkg` symbol package included.

[0.1.0]: https://github.com/keylight-dev/keylight-csharp/releases/tag/v0.1.0
