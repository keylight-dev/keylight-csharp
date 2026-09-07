# Unity UPM package — `dev.keylight.sdk`

## Package structure

```
unity/
  dev.keylight.sdk/
    package.json              UPM manifest (name, version, samples)
    Runtime/
      Keylight.asmdef         Assembly definition — compiled by Unity, autoReferenced
      Core/                   GENERATED — do not hand-edit (see below)
        Crypto/               Ed25519 + SHA-512 (pure C#, no UnityEngine refs)
        Json/                 Zero-dependency JSON codec
        *.cs                  Client, config, store, verifier, transport, …
      UnityLeaseStore.cs      Unity-specific ILeaseStore (Application.persistentDataPath)
      UnityWebRequestTransport.cs  WebGL-safe HTTP transport (UnityWebRequest)
      KeylightUnity.cs        Factory helper: wires store + transport + platform
    Samples~/
      Notes/
        KeylightNotesSample.cs  MonoBehaviour: activate key → gate Pro UI
        README.md
  sync-core.sh                Sync script (see below)
```

## Single source of truth: `src/Keylight/`

`Runtime/Core/` is **generated** by `unity/sync-core.sh`. It is committed so the
Unity Package Manager can install the package directly from the repo without a
build step, but the canonical source is `src/Keylight/`.

**Do not edit files under `Runtime/Core/` directly.** Edit `src/Keylight/` instead,
then regenerate:

```sh
./unity/sync-core.sh
```

The script uses `rsync` to wipe and re-copy all `.cs` files from `src/Keylight/`
(excluding `obj/`) into `Runtime/Core/`, preserving subdirectories (`Crypto/`, `Json/`).

## Unity-only files (hand-written, NOT synced)

All Unity-specific files are wrapped in `#if UNITY_2021_3_OR_NEWER` so they are
completely inert outside of a Unity project (the core `dotnet build` and tests
are unaffected).

| File | Purpose |
|------|---------|
| `Runtime/UnityLeaseStore.cs` | `ILeaseStore` backed by `Application.persistentDataPath` |
| `Runtime/UnityWebRequestTransport.cs` | WebGL-safe HTTP transport using `UnityWebRequest` |
| `Runtime/KeylightUnity.cs` | Factory: `KeylightUnity.CreateClient(config)` |

## Installing in a Unity project

### Via git URL (UPM)

Add to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "dev.keylight.sdk": "https://github.com/keylight-dev/keylight-csharp.git?path=unity/dev.keylight.sdk"
  }
}
```

Or use **Window > Package Manager > + > Add package from git URL** and paste:

```
https://github.com/keylight-dev/keylight-csharp.git?path=unity/dev.keylight.sdk
```

### Via OpenUPM

```sh
openupm add dev.keylight.sdk
```

## WebGL support

The default transport is **`UnityWebRequestTransport`** (not `HttpClient`).
`HttpClient` is unavailable on WebGL because the WASM runtime has no threading
primitives. `UnityWebRequest` is Unity's cross-platform HTTP stack and works on
every build target: Standalone, iOS, Android, WebGL, consoles.

`KeylightUnity.CreateClient(config)` wires everything up automatically — no
configuration is required to get WebGL support.

## Launch check: use `KeylightUnity.CheckOnLaunchAsync`

```csharp
var client = KeylightUnity.CreateClient(config);
await KeylightUnity.CheckOnLaunchAsync(client);   // not client.CheckOnLaunchAsync()
```

**Unity beacons at launch only.** The core's keyless heartbeat — which
`CheckOnLaunchAsync` normally starts, re-sending the anonymous beacon every six
hours — runs its ticks on a `System.Threading.Timer`, i.e. a thread-pool thread.
`UnityWebRequest` is main-thread-only, so a tick cannot send anything: it throws
where it builds the request, and the beacon (which never throws by contract)
swallows it. `KeylightUnity.CheckOnLaunchAsync` runs the launch check and then
calls `StopKeylessHeartbeat()`, so the behaviour matches what actually happens.

The launch beacon itself is unaffected — it is sent on the main thread, from your
`Start`, and carries the same trial length and free-tier flag as anywhere else.
An app that wants a mid-session beacon can call
`client.ReportKeylessStateAsync(...)` from main-thread code (a coroutine, an
`Update` timer). A coroutine-driven heartbeat inside the package is a follow-up.

## Ed25519 verification is pure-managed (IL2CPP-safe)

Lease signature verification uses a pure C# Ed25519 implementation
(`Runtime/Core/Crypto/`). There is no `DllImport`, no native `.so`/`.dylib`,
and no `System.Security.Cryptography` API that varies across runtimes. The
implementation is proven correct by **8 cross-SDK conformance vectors** executed
in CI on every commit, giving confidence that IL2CPP's AOT compilation does not
alter the verification result.

---

## Keylight Notes sample

The **Keylight Notes** sample (`Samples~/Notes/`) is a minimal MonoBehaviour
that shows the full activation + entitlement gate flow.

### Importing the sample

1. Open **Window > Package Manager**.
2. Find **Keylight** in the list.
3. Click **Samples > Keylight Notes > Import**.

The sample lands in `Assets/Samples/Keylight/<version>/Keylight Notes/`.

### What the sample does

- On Start, calls `KeylightUnity.CreateClient(config)` and
  `KeylightUnity.CheckOnLaunchAsync(client)` to refresh any cached lease.
- On button press, calls `await client.ActivateAsync(keyInput.text)`.
- In `Update()`, calls `client.HasEntitlement("pro")` (synchronous, safe on the
  main thread) to gate the visibility of the Pro panel.
- Shows `client.State` in a status label.

`HasEntitlement` and `State` are **synchronous** — they only read the local lease
cache. They are safe to call from `Update()`, UI callbacks, or any main-thread
code without `await`.

---

## IL2CPP + WebGL verification procedure

This is the de-risk checkpoint for platform correctness. Nicolas runs this
manually before a release.

### Prerequisites

- Unity 2021.3 LTS or later.
- IL2CPP build support module installed for your target platform.
- The `dev.keylight.sdk` package imported (git URL or local path override).
- The Keylight Notes sample imported (see above).

### Steps

1. **Import package and sample**
   - Add the package via git URL or local path override.
   - Import the **Keylight Notes** sample from the Package Manager.

2. **Configure the sample**
   - Select the `KeylightNotesSample` GameObject in the scene.
   - In the Inspector, fill in:
     - `Tenant Id` — your Keylight tenant ID.
     - `Product Id` — your Keylight product ID.
     - `Sdk Key` — your Keylight SDK key.
     - `Trusted Key Hex` — the hex-encoded Ed25519 public key for your product.
     - `Trusted Key Id` — the `kid` value in your leases (commonly `k1`).
   - Assign the UI references (`Key Input Field`, `Activate Button`,
     `Status Label`, `Pro Panel`).

3. **Build: Standalone IL2CPP player**
   - Go to **File > Build Settings**.
   - Select your platform (e.g. Windows, macOS, Linux).
   - Under **Player Settings > Other Settings**, set **Scripting Backend = IL2CPP**.
   - Click **Build and Run**.

4. **Build: WebGL player**
   - Switch platform to **WebGL**.
   - **Player Settings > Publishing Settings**: enable **Compression Format =
     Disabled** for easier local testing (or Gzip for production).
   - Click **Build and Run** — Unity will open a local web server.

5. **Run the verification**
   - In the running player (Standalone or WebGL):
     - Enter a valid license key in the input field.
     - Click **Activate**.
   - **Expected result — Standalone:** Status label shows `Licensed (Pro)` (or
     `Licensed`), Pro panel becomes visible. Activation result is cached to disk.
   - **Expected result — WebGL:** Same UI outcome. The network request goes through
     `UnityWebRequest`, which works in the browser's `XMLHttpRequest` layer.

6. **Confirm offline entitlement gate (Standalone only)**
   - Quit the player.
   - Disconnect from the network (or block `api.keylight.dev` in your firewall).
   - Relaunch the player.
   - `CheckOnLaunchAsync` will fail to refresh (network unreachable) but the
     cached lease is still trusted.
   - **Expected:** `HasEntitlement("pro")` still returns `true`, Pro panel is
     still visible. This confirms the offline-first Ed25519 path works under IL2CPP.

### What a passing result proves

| Check | Means |
|-------|-------|
| Activation succeeds in Standalone IL2CPP | `UnityWebRequest` POST + JSON parsing works after AOT compilation |
| Activation succeeds in WebGL | `UnityWebRequest` works in the browser runtime |
| Pro panel gated correctly after activation | `HasEntitlement` reads entitlements from the verified lease |
| Pro panel still gated after going offline | Ed25519 verify + local lease cache are IL2CPP-safe |
| 8 CI conformance vectors pass | Ed25519 pure-C# implementation is correct across platforms |
