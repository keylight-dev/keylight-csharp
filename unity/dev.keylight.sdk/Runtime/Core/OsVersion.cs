using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Keylight {
  /// <summary>
  /// The host OS release, reduced to the dotted-numeric shape the API accepts,
  /// or <c>null</c> when it cannot be read cleanly.
  ///
  /// <para><b>Why this is not just <c>Environment.OSVersion</c>.</b> On macOS
  /// that returns the DARWIN KERNEL version (<c>24.5.0</c>), not the marketing
  /// version (<c>15.5</c>) — a different vocabulary from the one the Swift
  /// (<c>ProcessInfo</c>), Rust and JS (<c>sw_vers</c>) SDKs send. Reporting it
  /// would split one macOS release across two families of bucket in the same
  /// breakdown: the same OS counted twice under two names. That exact bug was
  /// found and fixed in the JS SDK; this avoids repeating it.</para>
  ///
  /// <para>So macOS shells <c>sw_vers -productVersion</c>, once per process.
  /// Windows takes <c>Environment.OSVersion.Version</c>, which is already the
  /// build number every other SDK reports there. Linux reads the kernel release
  /// from <c>/proc/sys/kernel/osrelease</c>, matching Rust — distro versions
  /// live in <c>/etc/os-release</c> but are not comparable across distros,
  /// while the kernel is what OS-level behaviour actually tracks.</para>
  ///
  /// <para>Every probe is best-effort. Anything unreadable yields
  /// <c>null</c> and the field is omitted: the wire field is optional, and a
  /// wrong-vocabulary value mints a phantom bucket that looks like a real
  /// release, which is worse than an absent row.</para>
  /// </summary>
  internal static class OsVersion {
    private static readonly Lazy<string?> Cached = new Lazy<string?>(Probe);

    /// <summary>The dotted-numeric OS version, or <c>null</c>.</summary>
    public static string? Value => Cached.Value;

    /// <summary>
    /// Keep only the leading dotted-numeric run. The API nulls anything else,
    /// so a value that cannot be reduced is omitted at the source rather than
    /// sent as junk. A run longer than the server's cap is rejected rather than
    /// truncated — truncating would mint a fake version out of a client bug.
    /// </summary>
    internal static string? DottedNumeric(string? raw) {
      if (string.IsNullOrWhiteSpace(raw)) return null;
      var s = raw!.Trim();
      int end = 0;
      while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
      var run = s.Substring(0, end).TrimEnd('.');
      if (run.Length == 0 || run.Length > Telemetry.OsVersionMax) return null;
      // Reject "1..2" and a leading dot: every segment must be digits.
      foreach (var part in run.Split('.')) {
        if (part.Length == 0) return null;
        foreach (var c in part) if (!char.IsDigit(c)) return null;
      }
      return run;
    }

    private static string? Probe() {
      try {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return DottedNumeric(SwVers());
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
          return DottedNumeric(File.ReadAllText("/proc/sys/kernel/osrelease"));
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
          return DottedNumeric(Environment.OSVersion.Version.ToString());
        }
      } catch {
        // Best-effort by design; see the class remarks.
      }
      return null;
    }

    /// <summary>
    /// <c>sw_vers -productVersion</c>. Timed out so a wedged binary can never
    /// hold up an activation, and failures are swallowed.
    /// </summary>
    private static string? SwVers() {
      try {
        var psi = new ProcessStartInfo("sw_vers", "-productVersion") {
          RedirectStandardOutput = true, RedirectStandardError = true,
          UseShellExecute = false, CreateNoWindow = true,
        };
        using (var p = Process.Start(psi)) {
          if (p == null) return null;
          var stdout = p.StandardOutput.ReadToEnd();
          if (!p.WaitForExit(2000)) { try { p.Kill(); } catch { } return null; }
          return p.ExitCode == 0 ? stdout.Trim() : null;
        }
      } catch { return null; }
    }
  }
}
