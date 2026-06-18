# Keylight Notes — .NET console demo

A minimal .NET 8 console app showing the full Keylight activation flow:

1. Fetch the tenant's public keyset (`Keyset.FetchAsync`)
2. Build a `KeylightConfig` with the fetched keys as `TrustedKeys`
3. `ActivateAsync` a license key
4. Print `client.State` and `client.HasEntitlement("pro")`
5. Optionally `DeactivateAsync` with `--release`

## Run

```sh
KEYLIGHT_TENANT=your-tenant \
KEYLIGHT_PRODUCT=your-product \
KEYLIGHT_SDK_KEY=sdk_live_xxxx \
dotnet run --project samples/notes-console -- XXXX-XXXX-XXXX-XXXX
```

Add `--release` to deactivate the device after printing state:

```sh
KEYLIGHT_TENANT=your-tenant \
KEYLIGHT_PRODUCT=your-product \
KEYLIGHT_SDK_KEY=sdk_live_xxxx \
dotnet run --project samples/notes-console -- XXXX-XXXX-XXXX-XXXX --release
```

Run with no env vars or license key to print the usage message and exit cleanly.
