using System.Net.Http;
using Keylight;

// ── Keylight Notes — .NET console demo ──────────────────────────────────────
//
// Usage:
//   KEYLIGHT_TENANT=<tenant> KEYLIGHT_PRODUCT=<product> KEYLIGHT_SDK_KEY=<key> \
//     dotnet run --project samples/notes-console -- <license-key> [--release]
//
// --release   deactivates the device and frees the seat after printing state.
// ────────────────────────────────────────────────────────────────────────────

var tenant  = Environment.GetEnvironmentVariable("KEYLIGHT_TENANT");
var product = Environment.GetEnvironmentVariable("KEYLIGHT_PRODUCT");
var sdkKey  = Environment.GetEnvironmentVariable("KEYLIGHT_SDK_KEY");

// Filter out option flags to find the positional license-key argument.
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
var licenseKey = positional.Length > 0 ? positional[0] : null;
var release    = args.Contains("--release");

if (string.IsNullOrEmpty(tenant)     ||
    string.IsNullOrEmpty(product)    ||
    string.IsNullOrEmpty(sdkKey)     ||
    string.IsNullOrEmpty(licenseKey))
{
    Console.WriteLine("Keylight Notes -- .NET console demo");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  KEYLIGHT_TENANT=<tenant> \\");
    Console.WriteLine("  KEYLIGHT_PRODUCT=<product> \\");
    Console.WriteLine("  KEYLIGHT_SDK_KEY=<sdk-key> \\");
    Console.WriteLine("  dotnet run --project samples/notes-console -- <license-key> [--release]");
    Console.WriteLine();
    Console.WriteLine("  --release   deactivate this device and free the seat");
    return 0;
}

using var http = new HttpClient();

// 1. Fetch the tenant's public keyset so leases can be verified offline.
Console.WriteLine("Fetching keyset for tenant " + tenant + " ...");
var keyset = await Keyset.FetchAsync(http, baseUrl: "https://api.keylight.dev", tenantId: tenant);
if (keyset == null)
{
    Console.WriteLine("Error: could not fetch keyset. Check KEYLIGHT_TENANT and network access.");
    return 1;
}
Console.WriteLine("  keyset loaded (" + keyset.Keys.Count + " key(s))");

// 2. Build the client config, pinning the fetched public keys as trusted.
var config = KeylightConfig
    .Builder(tenant, product, sdkKey)
    .TrustedKeys(new Dictionary<string, string>(keyset.Keys))
    .AppVersion("1.0.0")
    .Platform("dotnet-console")
    .Build();

var client = new KeylightClient(config);

// 3. Activate the license key on this device.
Console.WriteLine("Activating license key " + licenseKey + " ...");
try
{
    await client.ActivateAsync(licenseKey);
}
catch (Exception ex)
{
    Console.WriteLine("Activation failed: " + ex.Message);
    return 1;
}

// 4. Print state and entitlement.
Console.WriteLine("  State           : " + client.State);
Console.WriteLine("  Has 'pro' plan  : " + client.HasEntitlement("pro"));

// 5. Optional deactivation (--release flag).
if (release)
{
    Console.WriteLine("Releasing device...");
    try
    {
        await client.DeactivateAsync();
        Console.WriteLine("Device removed. The seat is free.");
    }
    catch (Exception ex)
    {
        Console.WriteLine("Deactivation error: " + ex.Message);
        return 1;
    }
}

return 0;
