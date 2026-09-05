using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Keylight {
  /// <summary>
  /// Total installed physical RAM in bytes, or <c>0</c> when it cannot be
  /// determined. The byte figure never leaves the process — it is bucketed by
  /// <see cref="DeviceBuckets.BucketMemoryBytes"/> before anything is sent.
  ///
  /// <para><b>Why this is hand-rolled.</b> .NET has no cross-platform managed
  /// API for installed RAM. The obvious candidate,
  /// <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c>, reports the GC's
  /// heap limit — which is the cgroup memory limit inside a container and a
  /// runtime-configured fraction elsewhere, not the machine's RAM. Reporting it
  /// would put a 64GB build agent in the "&lt;4GB" bucket. It is also net5.0+,
  /// while this SDK targets netstandard2.0 for Unity.</para>
  ///
  /// <para>So each platform is probed with the API that actually answers the
  /// question: <c>GlobalMemoryStatusEx</c> on Windows, the
  /// <c>hw.memsize</c> sysctl on macOS, <c>MemTotal</c> from
  /// <c>/proc/meminfo</c> on Linux. Every probe is best-effort: any failure
  /// yields <c>0</c>, the bucket becomes <c>null</c>, and the <c>memory</c>
  /// field is simply omitted from the request. It is optional on the wire, and
  /// omitting beats guessing.</para>
  ///
  /// <para><b>Unity.</b> The whole native-interop path is compiled out under
  /// Unity. The Unity package's core is deliberately free of <c>DllImport</c>
  /// (WebGL cannot do native interop at all, and iOS IL2CPP needs
  /// <c>__Internal</c> rather than a library name), so a probe here would be a
  /// per-platform build hazard for a telemetry nicety. Unity instead feeds the
  /// value in from <c>UnityEngine.SystemInfo.systemMemorySize</c> via
  /// <see cref="SetTotalBytes"/>, which is Unity's own cross-platform answer.
  /// On Linux/Android the managed <c>/proc/meminfo</c> read still applies.</para>
  /// </summary>
  internal static class PhysicalMemory {
    private static long? _detected;
    private static long _explicit;

    /// <summary>
    /// Supplies the total physical RAM from a host that already knows it
    /// (Unity's <c>SystemInfo.systemMemorySize</c>). Takes precedence over
    /// probing. A non-positive value clears the override.
    /// </summary>
    internal static void SetTotalBytes(long bytes) {
      _explicit = bytes > 0 ? bytes : 0;
    }

    /// <summary>Installed RAM in bytes, or 0 if unknown. Probed once.</summary>
    internal static long TotalBytes {
      get {
        if (_explicit > 0) return _explicit;
        if (!_detected.HasValue) _detected = Detect();
        return _detected.Value;
      }
    }

    private static long Detect() {
      try {
#if !UNITY_2018_1_OR_NEWER
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return WindowsTotalBytes();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))     return MacTotalBytes();
#endif
        // Linux (and Android): pure managed, no interop, so it stays available
        // in the Unity package too.
        return ProcMeminfoTotalBytes();
      } catch {
        // Telemetry must never break licensing, and an unreadable /proc or a
        // sandbox that blocks the sysctl is a perfectly normal deployment.
        return 0;
      }
    }

    /// <summary>
    /// Parses <c>MemTotal:  16311456 kB</c> from <c>/proc/meminfo</c>. The unit
    /// is always kB on Linux (kernel writes it literally), so this is
    /// kibibytes, not the file's ambiguous label.
    /// </summary>
    private static long ProcMeminfoTotalBytes() {
      const string path = "/proc/meminfo";
      if (!File.Exists(path)) return 0;
      foreach (var line in File.ReadAllLines(path)) {
        if (!line.StartsWith("MemTotal:", StringComparison.Ordinal)) continue;
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return 0;
        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
          return 0;
        return kb * 1024L;
      }
      return 0;
    }

#if !UNITY_2018_1_OR_NEWER
    private static long WindowsTotalBytes() {
      var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
      if (!GlobalMemoryStatusEx(ref status)) return 0;
      // ullTotalPhys is installed RAM as the OS sees it — a few MB below the
      // sticker size on machines that reserve memory for firmware or an iGPU,
      // which is exactly why the buckets are compared against raw bytes rather
      // than a rounded gigabyte count.
      return status.ullTotalPhys > long.MaxValue ? 0 : (long)status.ullTotalPhys;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX {
      public uint dwLength;
      public uint dwMemoryLoad;
      public ulong ullTotalPhys;
      public ulong ullAvailPhys;
      public ulong ullTotalPageFile;
      public ulong ullAvailPageFile;
      public ulong ullTotalVirtual;
      public ulong ullAvailVirtual;
      public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>
    /// <c>sysctlbyname("hw.memsize")</c> — installed RAM in bytes. Readable
    /// from inside the App Sandbox (it is not a protected resource), and it is
    /// the same source Apple's own tooling reports.
    /// </summary>
    private static long MacTotalBytes() {
      long value = 0;
      var size = (IntPtr)sizeof(long);
      if (sysctlbyname("hw.memsize", ref value, ref size, IntPtr.Zero, IntPtr.Zero) != 0) return 0;
      return value > 0 ? value : 0;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int sysctlbyname(
      [MarshalAs(UnmanagedType.LPStr)] string name,
      ref long oldp, ref IntPtr oldlenp, IntPtr newp, IntPtr newlen);
#endif
  }
}
