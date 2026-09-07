# Changelog

All notable changes to the Keylight C# SDK are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.4.1] — 2026-09-07

### Added

- **`EffectiveFreeTierEnabled()`.** The free-tier flag has ridden on every
  validate and `/config` response since 0.3.0 and been cached, but nothing on
  the client returned it. Same precedence as `EffectiveTrialDurationDays()`:
  server value first, then the fallback — which is `false`, because
  `KeylightConfig` has never carried a free-tier seed. The SDK still only
  reports the flag; `KeylightState` has no free-tier member, so what a free
  tier unlocks remains the host app's decision.

- **`RefreshAfterUpgradeAsync(timeout?, pollInterval?, ct)`** — the post-purchase
  polling loop the Swift, JS, and Rust SDKs already have, with the same
  semantics. It snapshots the entitlement set and resolved `State` when called,
  then validates every `pollInterval` (default 2 s, floored at 100 ms) until
  either differs, returning `true` as soon as that happens; a definitive
  rejection that changes state counts. Transient validate failures are
  swallowed and polling continues. Returns `false` on timeout (default 30 s —
  the final delay is capped so the call never overruns it), on cancellation, or
  immediately and without a network call when no license is stored. Never
  throws.

### Fixed

- **The canonical config payload was formatted with the current culture.**
  `ConfigPayload.Canonical` interpolated `issuedAt`, `expiresAt`, and
  `trialDurationDays` with the process culture, so a locale that formats
  numbers differently (a non-ASCII minus sign under ICU, say) would produce
  bytes the worker never signed and reject a valid config. Every numeric field
  now uses `CultureInfo.InvariantCulture`, as `Store` already did; the golden
  vector is additionally pinned under a non-invariant culture.

- **`Verifier.VerifyConfig` could throw instead of returning `false`.** The
  trusted-key lookup sat outside the `try`, so a `null` `kid` or a `null` key
  dictionary surfaced as an exception on the response path. It now returns
  `false` for a null or empty `kid`, a null signature envelope, and null
  trusted keys — a malformed body is a rejected config, not a crash.

## [0.4.0] — 2026-09-06

### Added

- **`RequireSignedConfig` — Ed25519 verification of server-owned product
  settings.** The worker has signed the trial length and free-tier flag on every
  route that delivers them since 2026-09-06; nothing on the client checked those
  signatures. `Verifier.VerifyConfig` now does, over the frozen cross-SDK
  payload `cfg1|kid|tenant|product|issuedAt|expiresAt|days|freeTier`.

  **Off by default, and it should stay off unless you know your product is
  signed.** The worker signs a product's settings only once that product has a
  trial length configured in the dashboard; everything else is served unsigned,
  and enabling this against an unsigned product would reject legitimate
  responses and pin the install to its compiled-in seed.

  When enabled, a config that does not verify is never cached — the SDK falls
  back to your seed value, never to what the server claimed. The check lives at
  the single point where settings are merged, so neither `/config` nor
  `validate` can be used to write settings around it.

  Trust is rooted in the `TrustedKeys` you compile in. The SDK does not fetch a
  keyset at runtime: keys fetched over the same channel that serves the config
  would let anyone able to forge one forge the other. `Keyset.FetchAsync`
  remains a bootstrapping convenience, not a trust root. The trade-off is that
  rotating to a new `kid` leaves already-shipped builds on their last cached
  settings until they update — a freeze, not a failure.

  Verification is pinned by a golden vector captured from the live worker, the
  same one the Swift SDK pins, so both are proven to agree with production and
  with each other.

## [0.3.0] — 2026-09-05

### Added

- **`os_version` and `arch` on `activate` and `validate`.** This SDK was the only
  one of the five sending neither, so C# apps contributed nothing to two
  breakdowns every other SDK populates. `arch` is a canonical `arm64` /
  `x86_64` token (anything else is omitted rather than sent as junk, since the
  server allow-lists it); `os_version` is the dotted-numeric release.

  On macOS this is the **marketing** version from `sw_vers` (`15.5`), not
  `Environment.OSVersion`, which returns the Darwin kernel version (`24.5.0`) —
  a different vocabulary from the one the Swift and Rust SDKs send, which would
  split one macOS release across two families of bucket in the same breakdown.
  Windows uses the OS build; Linux the kernel release, matching Rust. Anything
  unreadable is omitted: the fields are optional, and a wrong-vocabulary value
  mints a phantom bucket that looks like a real release. No app code changes.

- **Coarse device-capability telemetry.** `activate` and `validate` now also
  send `cpu_cores` and `memory`. Both are **buckets**, never raw values —
  `"1-2" | "3-4" | "5-8" | "9-16" | "17+"` and
  `"<4GB" | "4-8GB" | "8-16GB" | "16-32GB" | "32-64GB" | "64GB+"` — so the exact
  core count and RAM size never leave the device. Both fields are optional and
  additive: `memory` is omitted entirely on platforms where installed RAM cannot
  be read. Nothing to do in your code.

## [0.2.0] — 2026-09-05

### Added

- **The trial length is the server's, not the build's.** A tenant could set a
  trial length in the dashboard and nothing happened to their C# app:
  `KeylightConfig.TrialDurationDays` was read directly wherever a trial decision
  was made, so the value compiled into the build was the only one that ever
  applied. New `KeylightClient.EffectiveTrialDurationDays()` resolves **server
  value → local seed → 0**, and state resolution reads it. The config value
  stays a *seed*, deliberately: a brand-new install genuinely has nothing else,
  and removing it would make first-launch behaviour depend on the network.
- **`KeylightClient.FetchConfigAsync()`** reads `GET /{tenant}/{product}/config`,
  and never throws. `CheckOnLaunchAsync` calls it **only when there is no active
  license**. This is a deliberate divergence from the other SDKs, which absorb
  the settings from the keyless beacon: this SDK has no beacon, so `/config` is
  the only route that ever reaches a trial user. Licensed installs skip it —
  `ValidateAsync` already carries the settings on a call it was making anyway.
- **The settings ride on `validate` too.** `ValidateResponse` now carries
  `trial_duration_days` and `free_tier_enabled`, absorbed regardless of whether
  the licence itself validated.
- **`sdk_trial_duration_days` on activate and validate** — the length the build
  was *configured* with, not the effective one, since echoing the server's own
  number back diagnoses nothing. Omitted entirely when unconfigured, rather than
  sent as a misleading `0`. Diagnostic only: the server must never gate on it,
  because a patched client sends whatever its author wants.
- **`IKeylightConfigTransport`**, an optional capability interface implemented by
  `HttpClientTransport`. Separate from `IKeylightTransport` so the addition is
  non-breaking — existing custom transports keep compiling, and the client skips
  the fetch when a transport does not implement it. A default interface method
  would have been tidier, but this assembly also targets netstandard2.0 (Unity),
  whose runtime cannot dispatch one.

### Fixed

- **The trial clock is now stamped even when no trial is on offer.**
  `CheckOnLaunchAsync` only persisted `TrialStartedAt` when `TrialDurationDays`
  was configured and above zero. Once the duration is server-owned, `0` is
  indistinguishable from "the config has not arrived yet", so that guard left no
  start timestamp for a later-arriving duration to measure — a tenant enabling a
  trial in the dashboard would find it did nothing for every install that had
  already launched. The stamp grants nothing on its own: state still resolves to
  `Invalid` while the effective duration is 0. An existing stamp is never
  overwritten, so enabling a trial 60 days after an install does not hand it a
  fresh window.
- **`0` is a real setting and absence is not zero.** `CachedState` stores the
  pair as nullables, written to the state file only when present, and merged
  field by field — so a worker that sends neither field leaves what the install
  already learned alone, a server `0` survives a relaunch as `0` rather than
  falling back to the seed, and `free_tier_enabled: false` is not mistaken for
  "never heard".
- **`ValidateAsync` no longer resets the cached settings.** It rebuilds
  `CachedState` from scratch on two paths, so the new fields are explicitly
  carried forward; a field omitted from that rebuild is a field silently reset
  to the seed on the next validate.

### Changed

- `TrialTests.No_TrialDurationDays_and_no_license_gives_Invalid` **inverts** its
  storage assertion. It used to require that no `TrialStartedAt` was written,
  which was right when the duration was compiled in and is wrong now. The
  user-visible property it also asserts — that the state is `Invalid` — is
  unchanged, and is what enforces "the stamp grants nothing".

### Notes

- `free_tier_enabled` is persisted for wire parity and so a later port inherits
  the plumbing, but is **currently unread**: this SDK has no free-tier state
  (`KeylightState` has no `FreeTier` member) and no keyless beacon.
- 94 → 111 tests.
- Ports the contract shipped in `keylight-cpp` 0.2.0/0.2.1; see that repo's
  `docs/superpowers/specs/2026-09-05-trial-parity-handoff.md`.

## [0.1.3] — 2026-08-01

### Added

- **The SDK now identifies itself on the wire.** `activate` and `validate` send
  `sdk: "csharp"` alongside the existing `platform` field. Since 0.1.2 fixed
  `Platform` to report canonical `macos`/`windows`/`linux` tokens, it became
  identical to what the Rust and C++ SDKs send and could no longer indicate
  which SDK a device was running. No API change and nothing to do in your code.

### Note

0.1.2 was tagged but never reached NuGet — its release run failed before
publishing. 0.1.3 is therefore the first published package, and it contains
everything listed under 0.1.2 below.

## [0.1.2] — 2026-07-29

### Added

- **`NeedsReactivation` — surfaces installs that can no longer check in.** A device that
  activated under an SDK build predating the persisted license key cannot call /validate,
  which requires `license_key` on the wire, and the key cannot be recovered locally (the
  cached lease carries only a hash). Such a device never refreshes its lease, so once the
  cached lease passes its 7-day expiry it drops to `Expired` and stays there — stranding a
  paying customer, and delaying any revocation by the same window. `ValidateAsync` skips the
  call silently because it is a hot path that must not throw, so this property makes the
  condition observable: check it at launch and prompt for the key. Re-running
  `ActivateAsync` clears it. Trial-only devices, which never activated, report `false`.

- **`ActiveRevalidateAsync()` — prompt revocation enforcement mid-session.** Forces a server
  validate on active use (app foreground, window focus, popover open), debounced to 60 seconds
  in memory. Unlike `RefreshIfNeededAsync` it is never skipped for staleness, so a dashboard
  revoke lands within minutes instead of waiting for the next launch. A definitive rejection
  (`valid:false`, including the HTTP 422 revoke shape) downgrades immediately; a transient or
  thrown failure leaves the session untouched. Mirrors the Swift SDK's `activeRevalidate()`.
  The Unity package (`dev.keylight.sdk`) gets the same primitive.

### Fixed

- **Activate/validate no longer fail on macOS and Linux.** The default `platform` telemetry
  field sent `RuntimeInformation.OSDescription`, which exceeds the API's 32-character cap on
  those platforms — the server rejected the whole request body with a 400. The SDK now sends
  a short OS token (`macos` / `windows` / `linux` / `unknown`), matching the Rust SDK. The
  hand-maintained Unity mirror (`dev.keylight.sdk`) carries the same fix.

- **Telemetry fields are clamped to the API's limits.** `app_version` and `sdk_version` are
  truncated to 64 and `platform` to 32 before being sent. An over-long value is rejected by
  the API with a 400 for the *whole* request — the field is not simply dropped — and
  `app_version` comes from the host app, so an app with a long version string could fail
  every activate and validate outright. Clamping happens at the wire boundary, so it also
  covers an explicit `ConfigBuilder.Platform(...)` override, which bypasses the canonical
  token above. Defence in depth for the same class of failure. Parity with Rust and JS.

- **The `ActiveRevalidateAsync()` debounce no longer follows the wall clock.** It now measures
  elapsed time from a monotonic source. Because the debounce *suppresses* revalidation, a
  system clock moved backwards previously suppressed revocation enforcement for the size of
  the jump — indefinitely, if the clock stayed back.

- **Unity package core regenerated from `src/Keylight/`.** `Runtime/Core/` is generated by
  `unity/sync-core.sh` and must never be hand-edited, but it had been patched by hand and had
  drifted: it reported `sdk_version` `0.1.0` in every activate/validate telemetry payload while
  the shipped package was `0.1.1`, it was missing `ConfigBuilder.Platform(...)` (the exact
  override `KeylightUnity.CreateClient` documents), it re-loaded the lease store on every state
  read instead of using the in-memory cache, it skipped the client-side `KeyPrefix` guard, and
  it carried dead serialization code. Re-synced, so the Unity package and the NuGet package are
  once again the same core.

### Internal

- `sdk_version` drift guard: a test now asserts `SdkInfo.Version` matches the package version
  from the csproj, so the two can no longer silently disagree and mis-attribute usage analytics.
- Deduplicated the lease-cache priming preamble that was copy-pasted across six call sites in
  `KeylightClient` into a single `Cached()` accessor, and folded the duplicated
  `MaxOfflineDays` bound check into one helper.

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
