using System;
using Keylight;
using Xunit;

namespace Keylight.Tests {

  /// <summary>
  /// `cpu_cores` and `memory` ride the SAME activate/validate payloads that
  /// already carry `platform` and `sdk` — there is no separate telemetry call.
  /// Both are OPTIONAL and ADDITIVE: an older worker ignores them, and a device
  /// whose memory probe fails must omit `memory` rather than send a wrong or
  /// placeholder value.
  ///
  /// Only the BUCKET may cross the wire. A licensing SDK reporting an exact RAM
  /// size or core count to a developer proxying their own app reads as
  /// fingerprinting — that is a product decision, so these tests also assert the
  /// raw values are absent.
  /// </summary>
  public class DeviceCapabilityTelemetryTests {

    private static readonly string[] CpuBuckets =
      { "1-2", "3-4", "5-8", "9-16", "17+" };
    private static readonly string[] MemoryBuckets =
      { "<4GB", "4-8GB", "8-16GB", "16-32GB", "32-64GB", "64GB+" };

    // ---- Wire shape ------------------------------------------------------

    [Fact]
    public void ActivateRequest_sends_cpu_cores_and_memory() {
      var json = new ActivateRequest {
        LicenseKey = "KL-TEST",
        InstanceName = "dev",
        CpuCores = "5-8",
        Memory = "16-32GB"
      }.ToJson();

      Assert.Contains("\"cpu_cores\":\"5-8\"", json);
      Assert.Contains("\"memory\":\"16-32GB\"", json);
    }

    [Fact]
    public void ValidateRequest_sends_cpu_cores_and_memory() {
      var json = new ValidateRequest {
        LicenseKey = "KL-TEST",
        InstanceId = "inst-1",
        CpuCores = "1-2",
        Memory = "<4GB"
      }.ToJson();

      Assert.Contains("\"cpu_cores\":\"1-2\"", json);
      Assert.Contains("\"memory\":\"<4GB\"", json);
    }

    [Fact]
    public void ActivateRequest_omits_the_fields_when_unset() {
      // Additive and optional: a null must serialize as an absent/null field,
      // never as an empty string or a guessed bucket.
      var json = new ActivateRequest { LicenseKey = "KL-TEST", InstanceName = "dev" }.ToJson();
      Assert.DoesNotContain("\"cpu_cores\":\"", json);
      Assert.DoesNotContain("\"memory\":\"", json);
    }

    [Fact]
    public void ValidateRequest_omits_the_fields_when_unset() {
      var json = new ValidateRequest { LicenseKey = "KL-TEST", InstanceId = "inst-1" }.ToJson();
      Assert.DoesNotContain("\"cpu_cores\":\"", json);
      Assert.DoesNotContain("\"memory\":\"", json);
    }

    [Fact]
    public void The_fields_are_clamped_to_the_api_cap() {
      // Both are z.string().max(16) on the worker; over-long values reject the
      // whole body with a 400. Nothing the SDK produces is over 16, but the
      // properties are public, so the clamp has to be at serialization time —
      // the same reason app_version is clamped in ToJson rather than at the
      // call site.
      var json = new ActivateRequest {
        LicenseKey = "KL-TEST",
        InstanceName = "dev",
        CpuCores = new string('9', 40),
        Memory = new string('8', 40)
      }.ToJson();

      Assert.Contains($"\"cpu_cores\":\"{new string('9', 16)}\"", json);
      Assert.Contains($"\"memory\":\"{new string('8', 16)}\"", json);
      Assert.DoesNotContain(new string('9', 17), json);
      Assert.DoesNotContain(new string('8', 17), json);
    }

    // ---- Device defaults -------------------------------------------------

    [Fact]
    public void Device_CpuCores_is_an_allow_listed_bucket() {
      Assert.Contains(Device.CpuCores, CpuBuckets);
    }

    [Fact]
    public void Device_Memory_is_an_allow_listed_bucket_or_null() {
      // Null is a legitimate outcome: there is no managed cross-platform API
      // for physical RAM, and the probe is allowed to fail (locked-down
      // sandbox, unreadable /proc, WebGL). Omitting beats guessing.
      if (Device.Memory != null) Assert.Contains(Device.Memory, MemoryBuckets);
    }

    [Fact]
    public void Device_never_puts_the_raw_core_count_or_byte_size_on_the_wire() {
      var json = new ActivateRequest {
        LicenseKey = "KL-TEST",
        InstanceName = "dev",
        CpuCores = Device.CpuCores,
        Memory = Device.Memory
      }.ToJson();

      // The exact processor count must not appear as its own field.
      Assert.DoesNotContain($"\"cpu_cores\":\"{Environment.ProcessorCount}\"", json);
      Assert.DoesNotContain("processor", json.ToLowerInvariant());
      // No byte-scale integer (10+ digits) anywhere in the body.
      Assert.DoesNotMatch(@"\d{10,}", json);
    }
  }
}
