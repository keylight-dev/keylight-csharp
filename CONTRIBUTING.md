# Contributing

## Development

```bash
dotnet restore
dotnet build
dotnet test
```

The security-critical lease verifier is gated by Keylight's frozen **cross-SDK conformance vectors**
(`tests/Keylight.Tests/ConformanceTests.cs`, vectors vendored from the canonical Rust SDK). Don't
loosen or skip them — a failing vector means the verifier has diverged from the rest of the SDK
family (Swift, Rust, JavaScript, C#).

Any change to verification logic (`src/Keylight/Verifier.cs`), lease wire format
(`src/Keylight/Wire.cs`), or the state machine (`src/Keylight/State.cs`) must keep all 8 conformance
vectors passing.

## Repo layout

```
src/Keylight/        Core library — targets netstandard2.0 and net8.0
tests/               xUnit test suite (targets net10.0)
unity/dev.keylight.sdk/  Unity UPM package — source synced from src/ via unity/sync-core.sh
samples/             Example projects (notes-console, godot-notes)
```

Run the sync script after touching core source files that Unity consumers need:

```bash
bash unity/sync-core.sh
```

## Releasing (maintainers)

```bash
# 1. Bump the version in BOTH src/Keylight/Keylight.csproj and src/Keylight/Version.cs.
# 2. Update CHANGELOG.md with a new ## [X.Y.Z] entry.
# 3. Verify locally:
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release

# 4. Commit, then tag — the tag fires the release workflow:
git commit -am "Release vX.Y.Z"
git push origin main
git tag -a vX.Y.Z -m "Release vX.Y.Z"
git push origin vX.Y.Z
```
