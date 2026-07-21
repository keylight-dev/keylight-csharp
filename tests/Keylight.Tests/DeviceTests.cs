using Xunit;
using Keylight;

namespace Keylight.Tests {
  public class DeviceTests {
    /// <summary>
    /// The API caps the `platform` field at 32 chars and rejects the whole
    /// request body past that. Sending RuntimeInformation.OSDescription (the
    /// old behavior) 400'd every activate/validate on macOS and Linux, so the
    /// platform must be one of the canonical cross-SDK tokens (Rust/C++ parity).
    /// </summary>
    [Fact]
    public void Platform_IsCanonicalCrossSdkToken() {
      Assert.Contains(Device.Platform, new[] { "macos", "windows", "linux", "unknown" });
    }

    [Fact]
    public void Platform_FitsApiFieldCap() {
      Assert.True(Device.Platform.Length <= 32);
    }
  }
}
