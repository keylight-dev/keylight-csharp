using System;
using System.Runtime.InteropServices;

namespace Keylight {
  /// <summary>
  /// Helpers for producing default device-identifying strings included in
  /// telemetry fields sent to the Keylight API.
  /// </summary>
  internal static class Device {
    /// <summary>
    /// Canonical platform token sent as <c>platform</c> in activate/validate
    /// requests (parity with Rust/C++: <c>macos</c>/<c>windows</c>/<c>linux</c>/
    /// <c>unknown</c>). Must stay a short fixed token — the API caps the field
    /// at 32 chars and rejects the whole request body past that, so a free-form
    /// OS description (the previous behavior) 400s every activate/validate on
    /// macOS and Linux.
    /// </summary>
    public static string Platform { get; } =
      RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "macos"
      : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
      : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)   ? "linux"
      : "unknown";

    /// <summary>
    /// CPU-core BUCKET for this machine, sent as <c>cpu_cores</c> — never the
    /// count itself. See <see cref="DeviceBuckets"/> for the cross-SDK bucket
    /// contract and why the raw value must not cross the wire.
    ///
    /// <c>Environment.ProcessorCount</c> is the logical-processor count as the
    /// runtime sees it (it honours CPU affinity and container CPU limits), which
    /// is the right number here: it describes the machine the app can actually
    /// use.
    /// </summary>
    public static string? CpuCores { get; } =
      DeviceBuckets.BucketCpuCores(Environment.ProcessorCount);

    /// <summary>
    /// Installed-RAM BUCKET for this machine, sent as <c>memory</c> — never the
    /// byte size. <c>null</c> when the probe could not answer (see
    /// <see cref="PhysicalMemory"/>); the field is optional and is then omitted
    /// rather than defaulted, because a default would misfile the device.
    ///
    /// Not cached in a static initializer: Unity supplies the value after
    /// startup via <c>PhysicalMemory.SetTotalBytes</c>, and the underlying probe
    /// caches itself anyway.
    /// </summary>
    public static string? Memory => DeviceBuckets.BucketMemoryBytes(PhysicalMemory.TotalBytes);

    /// <summary>
    /// A human-readable instance name for the current device, sent as
    /// <c>instance_name</c> during activation (display-only; seat identity is
    /// the server-issued <c>instance_id</c>).
    ///
    /// Preference order (mirrors Rust/JS parity):
    ///   1. HOSTNAME env var
    ///   2. COMPUTERNAME env var (Windows)
    ///   3. RuntimeInformation.OSDescription (coarse but always available)
    ///
    /// Evaluated once at process start (hostname is process-stable).
    /// </summary>
    public static readonly string DefaultInstanceName = ResolveInstanceName();

    private static string ResolveInstanceName() {
      var host = Environment.GetEnvironmentVariable("HOSTNAME")
        ?? Environment.GetEnvironmentVariable("COMPUTERNAME");
      return !string.IsNullOrEmpty(host) ? host
        : RuntimeInformation.OSDescription ?? "unknown-device";
    }
  }
}
