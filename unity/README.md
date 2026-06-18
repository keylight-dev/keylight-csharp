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
      KeylightUnity.cs        Factory helper: wires store + platform override
    Samples~/
      Notes/                  Placeholder — fleshed out in Task 14
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

| File | Purpose |
|------|---------|
| `Runtime/UnityLeaseStore.cs` | `ILeaseStore` backed by `Application.persistentDataPath` |
| `Runtime/KeylightUnity.cs` | Factory: `KeylightUnity.CreateClient(config)` |

Both are guarded with `#if UNITY_2021_3_OR_NEWER` so they are inert outside Unity.

## Installing in a Unity project

Add to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "dev.keylight.sdk": "https://github.com/keylight-dev/keylight-csharp.git?path=unity/dev.keylight.sdk"
  }
}
```

Or use **Window > Package Manager > + > Add package from git URL**.
