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
