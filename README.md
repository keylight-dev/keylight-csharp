# Keylight C# SDK

[![CI](https://github.com/keylight-dev/keylight-csharp/actions/workflows/ci.yml/badge.svg)](https://github.com/keylight-dev/keylight-csharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Keylight.svg)](https://www.nuget.org/packages/Keylight)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Targets](https://img.shields.io/badge/targets-netstandard2.0%20%C2%B7%20net8.0-success.svg)](#runtime-support)
[![Conformance](https://img.shields.io/badge/conformance-8%2F8%20cross--SDK%20vectors-success.svg)](#conformance)

Open-source C# SDK for [Keylight](https://keylight.dev) — license your .NET, Godot, and Unity apps
with online activation and offline Ed25519 license verification.

> **In one line:** a software-licensing SDK for C# — license-key activation and validation,
> entitlement/feature gating, trials, and tamper-resistant **offline license verification** (signed
> `v3` lease, Ed25519 + clock-skew tolerance) for .NET apps, Godot 4 (via NuGet), and Unity (UPM
> package coming). Async-native, dependency-light, and fully nullable-annotated.

## Why Keylight

Licensing shouldn't mean bolting a heavyweight, phone-home-or-die SDK onto your app.

- **Works offline.** The license is a signed lease your app verifies locally with Ed25519 — no
  network round-trip to gate a feature, no lockout when the machine is offline.
- **Tamper-resistant by design.** Entitlements live *inside* the signature; a forged or hand-edited
  lease can't pass verification without the tenant's private key.
- **Async-native, no surprises.** Every network operation is a `Task`-returning async method with
  `CancellationToken` support. `State` and `HasEntitlement` are synchronous reads from the in-memory
  cache — no deadlocks.
- **One SDK family.** Verifies licenses identically to the Swift, Rust, and JavaScript SDKs, proven
  by shared conformance vectors.

## Table of Contents

- [Why Keylight](#why-keylight)
- [Features](#features)
- [Runtime Support](#runtime-support)
- [Quick Start](#quick-start)
- [License Lifecycle](#license-lifecycle)
- [License States](#license-states)
- [Entitlements](#entitlements)
- [Offline Validation](#offline-validation)
- [Refresh and Trials](#refresh-and-trials)
- [Configuration Reference](#configuration-reference)
- [Godot and Unity](#godot-and-unity)
- [Conformance](#conformance)
- [Documentation](#documentation)
- [Other SDKs](#other-sdks)
- [License](#license)

## Features

- **License Lifecycle** — Activate, validate, and deactivate license keys with a small, explicit API.
- **Offline Verification** — The single offline artifact is a signed `v3` **lease**, verified with
  **Ed25519** and a 300-second clock-skew tolerance. An optional `MaxOfflineDays` grace caps how
  long a device may run without checking in.
- **Async-native** — All network operations are `Task`-returning with `CancellationToken` support.
  Synchronous wrappers (`Activate`, `Validate`, `Deactivate`) are provided for non-async contexts.
- **Synchronous reads** — `State` and `HasEntitlement` are synchronous, backed by the in-memory
  lease cache — no deadlocks from sync-over-async.
- **Entitlements** — Feature gating from the cached lease: `client.HasEntitlement("pro")`.
- **Trials** — Built-in local trial timer, auto-started on first launch via `CheckOnLaunchAsync`.
- **Device Telemetry** — Auto-attaches `sdk_version`, `platform`, and (optional) `app_version`
  on every API call.
- **Network Resilience** — Validate network failures are non-fatal (the client retains whatever
  state the cached lease dictates).
- **Pluggable** — Swap the storage backend (`ILeaseStore`) or HTTP transport (`IKeylightTransport`)
  via interfaces for tests or custom platforms.
- **Nullable-annotated** — Targets `netstandard2.0` and `net8.0`; ships with `#nullable enable`
  throughout and an `.snupkg` symbol package.

## Runtime Support

Targets **`netstandard2.0`** (broad compatibility: .NET Framework 4.6.1+, .NET Core 2.0+, Mono,
Godot 4 via NuGet) and **`net8.0`**. The core library has **zero runtime dependencies** — pure
managed code with no NuGet references (important for IL2CPP/WebGL targets).

A Unity UPM package (`dev.keylight.sdk`) is available — see [Godot and Unity](#godot-and-unity).

## Quick Start

```bash
dotnet add package Keylight
```

```csharp
using System.Net.Http;
using Keylight;

// Fetch the tenant's trusted Ed25519 keyset so leases can be verified offline.
// (You can also pin keys explicitly via .TrustedKeys(...) on the builder.)
var http = new HttpClient();
var keyset = await Keyset.FetchAsync(http, "https://api.keylight.dev", "your-tenant");

var config = KeylightConfig
    .Builder("your-tenant", "your-product", "sdk_live_…")
    .TrustedKeys(keyset?.Keys ?? new Dictionary<string, string>())
    .MaxOfflineDays(15)  // optional offline grace window (15 is the default)
    .Build();

var client = new KeylightClient(config);

// Activate a license key (online). The returned lease is Ed25519-verified
// *before* anything is persisted.
await client.ActivateAsync("USER-LICENSE-KEY");

// Gate features on entitlements — synchronous, from the cached lease.
if (client.HasEntitlement("pro"))
{
    // unlock pro features
}

// Release the seat when uninstalling / switching devices.
await client.DeactivateAsync();
```

> Call `await client.CheckOnLaunchAsync()` on startup to refresh the lease if it is stale, and to
> auto-start the trial clock on first launch when `TrialDurationDays` is configured.

## License Lifecycle

```
┌─────────────────┐     ┌──────────────────┐     ┌──────────────────┐
│  ActivateAsync  │────▶│  ValidateAsync   │────▶│  DeactivateAsync │
└─────────────────┘     └──────────────────┘     └──────────────────┘
                                ▲
                                │ on launch / on events (no background timers)
                      ┌──────────────────────────┐
                      │  RefreshIfNeededAsync     │
                      └──────────────────────────┘
```

| Method | Description |
|--------|-------------|
| `ActivateAsync(key)` | Activates a key on this device. Verifies the returned lease before persisting; throws `ActivationException` on server error and `LeaseVerificationFailedException` on a bad signature. |
| `ValidateAsync()` | Re-checks the stored license online. Updates the cache if the server returns a new lease; network failures are non-fatal. |
| `DeactivateAsync()` | Releases the seat and clears local license state, even if the network call fails. Call on uninstall or device switch. |
| `RefreshIfNeededAsync()` | Validates only if due (debounce 5 min, stale 6 h, or within 24 h of expiry). Safe to call often. |
| `CheckOnLaunchAsync()` | Convenience: refresh if a license is stored; also auto-starts the trial clock on first launch. |
| `ActiveRevalidateAsync()` | Forces a validate on active use (foreground / popover / focus), debounced 60 s in memory. Bypasses the staleness gates so a revoke lands mid-session instead of at the next launch. Never throws; a transient failure never downgrades a live session. |

Synchronous wrappers `Activate(key)`, `Validate()`, and `Deactivate()` are provided for callers
that cannot use `async`/`await` (every `await` in the async path uses `ConfigureAwait(false)`).

## License States

`client.State` resolves a single high-level status from the cached, signature-verified lease
(no network call). It is a `KeylightState` enum:

| State | Meaning |
|-------|---------|
| `Licensed` | Current, signature-valid `active` lease. |
| `Trial` | No license, but a local trial is active. |
| `Expired` | Lease expired, or a previously stored license is no longer current. Also mapped from lease `status: "fallback"` (cross-SDK note: Swift and Rust surface a distinct `Limited` state; C# maps it to `Expired`). |
| `Invalid` | No valid lease and no trial in progress. |

```csharp
switch (client.State)
{
    case KeylightState.Licensed:
        // full access
        break;
    case KeylightState.Trial:
        // trial UI
        break;
    case KeylightState.Expired:
    case KeylightState.Invalid:
        // prompt to activate
        break;
}
```

## Entitlements

Entitlements are feature keys carried inside the signed lease and checked offline:

```csharp
if (client.HasEntitlement("cloud-sync"))
{
    EnableCloudSync();
}
```

`HasEntitlement` returns `true` only when the cached lease is signature-valid, unexpired, and not
`expired`-status — so offline feature gating never disagrees with the resolved `Expired` state.
When `MaxOfflineDays` is set, it also gates on the offline grace window.

## Offline Validation

The offline artifact is a signed **`v3` lease** issued by the Keylight API. The SDK reconstructs
the exact signed payload (entitlements sorted, pipe-delimited) and verifies it with **Ed25519**
against the tenant's trusted keyset, applying a **300-second clock-skew** tolerance.

```csharp
// Pin trusted keys explicitly instead of fetching them:
var config = KeylightConfig
    .Builder("your-tenant", "your-product", "sdk_live_…")
    .TrustedKeys(new Dictionary<string, string>
    {
        { "k1", "<raw Ed25519 public key, base64>" }
    })
    .MaxOfflineDays(15)  // default; omit to run offline as long as the lease itself is current
    .Build();
```

- The trusted keyset can be fetched once with `Keyset.FetchAsync(http, baseUrl, tenantId)` or
  pinned at build time via `.TrustedKeys(...)`.
- `client.State` and `client.HasEntitlement` read from the in-memory verified-lease cache — no
  network call.

## Refresh and Trials

There are **no background timers**. The host drives refresh on launch and on meaningful events:

```csharp
await client.CheckOnLaunchAsync();    // validate if due + auto-start trial clock
await client.RefreshIfNeededAsync();  // call again on window-focus / purchase / resume
await client.ActiveRevalidateAsync(); // app came forward: force a check (60 s debounce)
```

`RefreshIfNeededAsync` is the cheap, often-called path — it skips the server when the cache is
fresh. `ActiveRevalidateAsync` is the prompt one: it always talks to the server (debounced to
60 s) so a dashboard revoke takes effect within minutes of the user touching the app rather than
waiting for the lease to expire or the app to relaunch. Wire it to whatever "the user is here
now" signal your host has — app activation, window focus, menu-bar popover opening.

Trials are local and offline-first. Set `TrialDurationDays` on the builder, then call
`CheckOnLaunchAsync` — the trial clock is started automatically on the first launch when no trusted
active license is present:

```csharp
var config = KeylightConfig
    .Builder("your-tenant", "your-product", "sdk_live_…")
    .TrialDurationDays(14)
    .Build();

var client = new KeylightClient(config);
await client.CheckOnLaunchAsync();  // starts the trial on first launch

if (client.State == KeylightState.Trial)
{
    ShowTrialBanner();
}
```

## Configuration Reference

Built with `KeylightConfig.Builder(tenantId, productId, sdkKey)`:

| Builder method | Type | Default | Description |
|----------------|------|---------|-------------|
| _(required)_ `Builder(tenantId, productId, sdkKey)` | `string` | — | Your Keylight tenant, product, and SDK key. All three are required. |
| `.TrustedKeys(dict)` | `IDictionary<string,string>` | empty | Trusted Ed25519 public keys (`kid → base64`) for offline verification. |
| `.MaxOfflineDays(n)` | `int` | `15` | Offline grace window since last online validation. Set `0` to run offline as long as the lease itself is current. |
| `.TrialDurationDays(n)` | `int` | — | Local trial length in days. Omit to disable trials. |
| `.AppVersion(v)` | `string` | — | Reported in activation/validation telemetry. |
| `.KeyPrefix(p)` | `string` | — | Client-side key-format check (e.g. `"PROD"`). |
| `.BaseUrl(url)` | `string` | `https://api.keylight.dev` | API base URL. |

## Godot and Unity

**Godot 4** — install via the standard NuGet workflow. Add `Keylight` to your project's
`.csproj` or use the Godot editor's NuGet integration. The `netstandard2.0` target is compatible
with Godot's .NET 6+ Mono runtime.

**Unity** — a UPM (Unity Package Manager) package (`dev.keylight.sdk`) is included in this repo
under `unity/dev.keylight.sdk`. Install it via Unity's Package Manager (UPM) using the Git URL, or
via [OpenUPM](https://openupm.com) once published. The package source is kept in sync with
`src/Keylight` via `unity/sync-core.sh`.

## Conformance

The security-critical lease verifier is gated by Keylight's frozen **cross-SDK conformance vectors**
(`tests/Keylight.Tests/ConformanceTests.cs`). The C# verifier must agree with every vector on
`{ KidKnown, SignatureValid, Expired }`, which keeps offline verification behavior identical across
the Keylight SDK family (Swift, Rust, JavaScript, C#, …).

```bash
dotnet test tests/Keylight.Tests
```

## Documentation

- **Platform docs:** [docs.keylight.dev](https://docs.keylight.dev)
- **Website:** [keylight.dev](https://keylight.dev)
- **API host:** `https://api.keylight.dev`

## Other SDKs

| Platform | Status | Repository |
|----------|--------|------------|
| Swift (macOS/iOS) | Available | [keylight-swift](https://github.com/keylight-dev/keylight-swift) |
| Rust (CLIs/daemons/Tauri) | Available | [keylight-rust](https://github.com/keylight-dev/keylight-rust) |
| JavaScript/TypeScript | Available | [keylight-js](https://github.com/keylight-dev/keylight-js) |
| C# (this repo) | Available | [keylight-csharp](https://github.com/keylight-dev/keylight-csharp) |
| Unity (UPM) | Available | [keylight-csharp](https://github.com/keylight-dev/keylight-csharp) — `unity/dev.keylight.sdk` |
| C++ | Planned | unified by the same cross-SDK conformance vectors |

## License

MIT License. See [LICENSE](LICENSE) for details.

---

<sub>Keylight C# SDK — software licensing for .NET: license-key activation & validation, offline
Ed25519 lease verification, entitlement/feature gating, trials, and pluggable storage/transport —
for .NET, Godot 4 (NuGet), and Unity (UPM coming).</sub>
