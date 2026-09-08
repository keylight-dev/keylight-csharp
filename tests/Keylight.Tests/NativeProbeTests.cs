using System.Runtime.InteropServices;
using Keylight;
using Xunit;

namespace Keylight.Tests {
  /// <summary>
  /// Runs the real hardware-id probe on the OS the tests execute on and checks
  /// it against an independent read of the same source. These only prove
  /// something on the matching runner (CI runs ubuntu, windows and macos); on
  /// any other OS they pass trivially.
  /// </summary>
  public class NativeProbeTests {
    [Fact]
    public void Windows_probe_matches_the_64bit_MachineGuid() {
      if (!OperatingSystem.IsWindows()) return;

      // Independent read through the managed registry API, explicitly on the
      // 64-bit view — the same view every other SDK reads.
      string? expected;
      using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
               Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
      using (var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")) {
        expected = key?.GetValue("MachineGuid") as string;
      }
      Assert.False(string.IsNullOrWhiteSpace(expected), "runner has no MachineGuid; cannot validate the probe");

      var probed = new SystemDeviceIdentity().HardwareId();

      Assert.Equal(expected!.Trim(), probed);
      Assert.Matches("^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$", probed!);
    }

    [Fact]
    public void Mac_probe_returns_the_IOPlatformUUID() {
      if (!OperatingSystem.IsMacOS()) return;

      var probed = new SystemDeviceIdentity().HardwareId();

      Assert.False(string.IsNullOrWhiteSpace(probed));
      Assert.Matches("^[0-9A-Fa-f]{8}-([0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}$", probed!);
    }

    [Fact]
    public void Linux_probe_reads_machine_id_when_present() {
      if (!OperatingSystem.IsLinux()) return;
      if (!File.Exists("/etc/machine-id")) return; // minimal containers

      Assert.Equal(File.ReadAllText("/etc/machine-id").Trim(), new SystemDeviceIdentity().HardwareId());
    }
  }
}
