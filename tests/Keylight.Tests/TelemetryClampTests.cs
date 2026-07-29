using System.Collections.Generic;
using Keylight;
using Xunit;

namespace Keylight.Tests {

  /// <summary>
  /// The API caps `app_version`/`sdk_version` at 64 chars and `platform` at 32,
  /// and rejects the WHOLE request body with a 400 when one is over — the field
  /// is not dropped. `AppVersion` comes from the host app and is outside the
  /// SDK's control, so an app shipping a long version string would 400 every
  /// activate and validate. That is the same failure that the OSDescription
  /// platform bug caused on macOS and Linux.
  ///
  /// These assert against the SERVER's caps, not the SDK's own request shape —
  /// the distinction that let the missing `license_key` ship green.
  /// </summary>
  public class TelemetryClampTests {

    private const int VersionMax = 64;
    private const int PlatformMax = 32;

    [Fact]
    public void ActivateRequest_clamps_an_over_long_app_version() {
      var json = new ActivateRequest {
        LicenseKey = "KL-TEST",
        InstanceName = "dev",
        AppVersion = new string('9', 200)
      }.ToJson();

      Assert.Contains($"\"app_version\":\"{new string('9', VersionMax)}\"", json);
      Assert.DoesNotContain(new string('9', VersionMax + 1), json);
    }

    [Fact]
    public void ValidateRequest_clamps_an_over_long_app_version() {
      var json = new ValidateRequest {
        LicenseKey = "KL-TEST",
        InstanceId = "inst-1",
        AppVersion = new string('9', 200)
      }.ToJson();

      Assert.Contains($"\"app_version\":\"{new string('9', VersionMax)}\"", json);
      Assert.DoesNotContain(new string('9', VersionMax + 1), json);
    }

    /// <summary>
    /// ConfigBuilder.Platform(...) is an explicit caller override and bypasses
    /// the canonical Device.Platform token entirely, so the clamp has to sit at
    /// the wire boundary rather than on the default value.
    /// </summary>
    [Fact]
    public void An_over_long_platform_override_is_clamped() {
      var osDescription = "Linux 5.15.0-91-generic #101-Ubuntu SMP Tue Nov 14 18:15:26 UTC 2023";
      var json = new ValidateRequest {
        LicenseKey = "KL-TEST",
        InstanceId = "inst-1",
        Platform = osDescription
      }.ToJson();

      Assert.Contains($"\"platform\":\"{osDescription.Substring(0, PlatformMax)}\"", json);
      Assert.DoesNotContain(osDescription, json);
    }

    [Fact]
    public void Values_within_the_caps_pass_through_untouched() {
      var json = new ValidateRequest {
        LicenseKey = "KL-TEST",
        InstanceId = "inst-1",
        AppVersion = "1.2.3",
        SdkVersion = "0.1.1",
        Platform = "linux"
      }.ToJson();

      Assert.Contains("\"app_version\":\"1.2.3\"", json);
      Assert.Contains("\"sdk_version\":\"0.1.1\"", json);
      Assert.Contains("\"platform\":\"linux\"", json);
    }

    [Fact]
    public void Null_telemetry_stays_null_rather_than_becoming_empty() {
      Assert.Null(Telemetry.Clamp(null, VersionMax));
    }

    /// <summary>
    /// zod's .max() counts UTF-16 code units and so does string.Length, so the
    /// clamp must agree with the server exactly — but it must not cut a
    /// surrogate pair in half, which would put an unpaired code unit on the
    /// wire. An emoji is two code units, so 64 of them is 128: clamping lands
    /// exactly on a pair boundary here.
    /// </summary>
    [Fact]
    public void Clamping_never_splits_a_surrogate_pair() {
      var emoji = string.Concat(System.Linq.Enumerable.Repeat("\U0001F600", 64));
      var clamped = Telemetry.Clamp(emoji, VersionMax)!;

      Assert.True(clamped.Length <= VersionMax);
      Assert.False(char.IsHighSurrogate(clamped[clamped.Length - 1]),
        "clamped value must not end on a dangling high surrogate");
    }

    /// <summary>An odd cut point is the case that actually exercises the guard.</summary>
    [Fact]
    public void Clamping_backs_off_when_the_cap_lands_mid_pair() {
      // 1 leading ASCII char + emoji pairs puts a pair boundary off-by-one from
      // the cap, so a naive Substring(0, max) would slice a pair in half.
      var value = "a" + string.Concat(System.Linq.Enumerable.Repeat("\U0001F600", 64));
      var clamped = Telemetry.Clamp(value, VersionMax)!;

      Assert.False(char.IsHighSurrogate(clamped[clamped.Length - 1]));
      Assert.Equal(VersionMax - 1, clamped.Length);
    }
  }
}
