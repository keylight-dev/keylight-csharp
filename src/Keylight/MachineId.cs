using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Keylight {
  /// <summary>
  /// The cross-SDK <c>machine_hash</c>: lets the server dedupe keyless devices
  /// by a true hardware identifier instead of a per-install random id. Only
  /// ever computed from a real hardware id — callers omit the field when
  /// there is none. Must match byte-for-byte across all Keylight SDKs
  /// (keylight-rust <c>machine.rs</c>, keylight-cpp <c>machine_id.hpp</c>).
  /// </summary>
  internal static class MachineId {
    internal static string Hash(string tenantId, string productId, string stableId) {
      var material = "keylight-keyless-machine-v1|" + tenantId + "|" + productId + "|" + stableId;
      using var sha = SHA256.Create();
      var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
      var sb = new StringBuilder(64);
      foreach (var b in digest) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
      return sb.ToString();
    }
  }

  /// <summary>
  /// Source of the true OS/hardware machine id used for <c>machine_hash</c>.
  /// Return <c>null</c> when the platform has none. Never return a random
  /// fallback: it would defeat the cross-install dedupe the hash exists for.
  /// </summary>
  public interface IDeviceIdentity {
    string? HardwareId();
  }

  /// <summary>
  /// Default <see cref="IDeviceIdentity"/>: IOPlatformUUID on macOS, the
  /// Cryptography MachineGuid on Windows (64-bit registry view), machine-id on
  /// Linux. Under Unity the native probes are compiled out and the host feeds
  /// the value via <see cref="SetHardwareId"/> (see <c>KeylightUnity.CreateClient</c>).
  /// </summary>
  public sealed class SystemDeviceIdentity : IDeviceIdentity {
    private static string? _explicit;

    /// <summary>Host-supplied id (Unity's <c>SystemInfo.deviceUniqueIdentifier</c>).
    /// Blank, whitespace or Unity's <c>"n/a"</c> placeholder clears the override.</summary>
    public static void SetHardwareId(string? id) {
      var t = id?.Trim();
      _explicit = string.IsNullOrEmpty(t) || t == "n/a" ? null : t;
    }

    public string? HardwareId() {
      if (_explicit != null) return _explicit;
      try {
#if !UNITY_2018_1_OR_NEWER
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))     return Normalize(MacPlatformUuid());
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return Normalize(WindowsMachineGuid());
#endif
        return Normalize(ReadFileTrimmed("/etc/machine-id") ?? ReadFileTrimmed("/var/lib/dbus/machine-id"));
      } catch {
        return null; // identity is a telemetry nicety; it must never break licensing
      }
    }

    private static string? Normalize(string? s) {
      var t = s?.Trim();
      return string.IsNullOrEmpty(t) ? null : t;
    }

    private static string? ReadFileTrimmed(string path) {
      if (!File.Exists(path)) return null;
      return File.ReadAllText(path).Trim();
    }

#if !UNITY_2018_1_OR_NEWER
    // ─── macOS: IOKit IOPlatformExpertDevice / IOPlatformUUID ───────────────
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint kCFStringEncodingUTF8 = 0x08000100;

    [DllImport(IOKit)] private static extern IntPtr IOServiceMatching(string name);
    [DllImport(IOKit)] private static extern uint IOServiceGetMatchingService(uint masterPort, IntPtr matching);
    [DllImport(IOKit)] private static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);
    [DllImport(IOKit)] private static extern int IOObjectRelease(uint obj);
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, uint encoding);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, long bufferSize, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);

    private static string? MacPlatformUuid() {
      var svc = IOServiceGetMatchingService(0 /* MACH_PORT_NULL */, IOServiceMatching("IOPlatformExpertDevice"));
      if (svc == 0) return null;
      var key = CFStringCreateWithCString(IntPtr.Zero, "IOPlatformUUID", kCFStringEncodingUTF8);
      try {
        var prop = IORegistryEntryCreateCFProperty(svc, key, IntPtr.Zero, 0);
        if (prop == IntPtr.Zero) return null;
        try {
          var buf = new byte[128];
          if (!CFStringGetCString(prop, buf, buf.Length, kCFStringEncodingUTF8)) return null;
          var len = Array.IndexOf(buf, (byte)0);
          return System.Text.Encoding.UTF8.GetString(buf, 0, len < 0 ? buf.Length : len);
        } finally { CFRelease(prop); }
      } finally {
        CFRelease(key);
        IOObjectRelease(svc);
      }
    }

    // ─── Windows: HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid ──────────
    private const uint RRF_RT_REG_SZ = 0x00000002;
    // Forces the 64-bit registry view. Without it a 32-bit host is redirected
    // and reads a DIFFERENT MachineGuid than the other SDKs.
    private const uint RRF_SUBKEY_WOW6464KEY = 0x00010000;
    private static readonly IntPtr HKEY_LOCAL_MACHINE = new IntPtr(unchecked((int)0x80000002));

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegGetValueW(IntPtr hkey, string subKey, string value, uint flags,
      out uint type, byte[]? data, ref uint dataSize);

    private static string? WindowsMachineGuid() {
      var buf = new byte[512];
      uint size = (uint)buf.Length;
      var rc = RegGetValueW(HKEY_LOCAL_MACHINE, @"SOFTWARE\Microsoft\Cryptography", "MachineGuid",
        RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, out _, buf, ref size);
      if (rc != 0 || size < 2) return null;
      return System.Text.Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
    }
#endif
  }
}
