using System;
using System.Runtime.InteropServices;

namespace Keylight {
  /// <summary>
  /// Helpers for producing default device-identifying strings included in
  /// telemetry fields sent to the Keylight API.
  /// </summary>
  internal static class Device {
    /// <summary>
    /// Coarse OS description string sent as <c>platform</c> in activate/validate
    /// requests. Uses <see cref="RuntimeInformation.OSDescription"/> so it
    /// identifies the host OS without sending full User-Agent.
    /// </summary>
    public static string Platform { get; } = RuntimeInformation.OSDescription ?? "unknown";

    /// <summary>
    /// A human-readable instance name for the current device, sent as
    /// <c>instance_name</c> during activation (display-only; seat identity is
    /// the server-issued <c>instance_id</c>).
    ///
    /// Preference order (mirrors Rust/JS parity):
    ///   1. HOSTNAME env var
    ///   2. COMPUTERNAME env var (Windows)
    ///   3. RuntimeInformation.OSDescription (coarse but always available)
    /// </summary>
    public static string DefaultInstanceName() {
      var host = Environment.GetEnvironmentVariable("HOSTNAME")
        ?? Environment.GetEnvironmentVariable("COMPUTERNAME");
      if (!string.IsNullOrEmpty(host)) return host;
      return RuntimeInformation.OSDescription ?? "unknown-device";
    }
  }
}
