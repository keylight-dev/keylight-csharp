using Keylight;
using Xunit;

namespace Keylight.Tests {
  public class DeviceIdentityTests {
    [Fact]
    public void Host_override_wins_over_probing() {
      try {
        SystemDeviceIdentity.SetHardwareId("unity-device-42");
        Assert.Equal("unity-device-42", new SystemDeviceIdentity().HardwareId());
      } finally {
        SystemDeviceIdentity.SetHardwareId(null);
      }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("n/a")]
    public void Blank_or_placeholder_override_is_treated_as_absent(string value) {
      try {
        SystemDeviceIdentity.SetHardwareId(value);
        // With the override cleared to "absent" the probe runs; on CI it may or
        // may not find an id, so only assert the override itself was rejected.
        Assert.NotEqual(value, new SystemDeviceIdentity().HardwareId());
      } finally {
        SystemDeviceIdentity.SetHardwareId(null);
      }
    }

    [Fact]
    public void Probe_never_returns_an_empty_string() {
      var id = new SystemDeviceIdentity().HardwareId();
      Assert.True(id == null || id.Trim().Length > 0);
    }
  }
}
