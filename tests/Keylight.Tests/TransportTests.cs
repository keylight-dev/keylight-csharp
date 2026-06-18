using Keylight;
using Xunit;

/// <summary>
/// Tests for request serialization and response parsing via the internal wire codec.
/// System.Text.Json is intentionally NOT used here — these tests exercise the
/// zero-dependency JsonCodec that ships in the core (Unity IL2CPP safe).
/// </summary>
public class TransportTests {

  [Fact]
  public void ActivateRequest_serializes_snake_case_fields() {
    var req = new ActivateRequest {
      LicenseKey = "K",
      InstanceName = "Mac",
      AppVersion = "1.0",
      SdkVersion = "0.1.0",
      Platform = "macOS"
    };

    var json = req.ToJson();

    Assert.Contains("\"license_key\":\"K\"", json);
    Assert.Contains("\"instance_name\":\"Mac\"", json);
    Assert.Contains("\"app_version\":\"1.0\"", json);
    Assert.Contains("\"sdk_version\":\"0.1.0\"", json);
    Assert.Contains("\"platform\":\"macOS\"", json);
  }

  [Fact]
  public void ActivateRequest_omits_null_free_tier_instance_id() {
    var req = new ActivateRequest {
      LicenseKey = "K",
      InstanceName = "Mac"
    };

    var json = req.ToJson();

    Assert.DoesNotContain("free_tier_instance_id", json);
  }

  [Fact]
  public void ActivateRequest_includes_free_tier_instance_id_when_set() {
    var req = new ActivateRequest {
      LicenseKey = "K",
      InstanceName = "Mac",
      FreeTierInstanceId = "ft-123"
    };

    var json = req.ToJson();

    Assert.Contains("\"free_tier_instance_id\":\"ft-123\"", json);
  }

  [Fact]
  public void ValidateRequest_serializes_required_fields() {
    var req = new ValidateRequest {
      InstanceId = "inst-abc"
    };

    var json = req.ToJson();

    Assert.Contains("\"instance_id\":\"inst-abc\"", json);
    Assert.DoesNotContain("app_version", json);
    Assert.DoesNotContain("sdk_version", json);
    Assert.DoesNotContain("platform", json);
  }

  [Fact]
  public void DeactivateRequest_serializes_both_fields() {
    var req = new DeactivateRequest {
      InstanceId = "inst-xyz"
    };

    var json = req.ToJson();

    Assert.Contains("\"instance_id\":\"inst-xyz\"", json);
  }

  [Fact]
  public void ActivateResponse_deserializes_activated_and_instance_id() {
    var json = "{\"activated\":true,\"instance_id\":\"id-1\",\"license_expires_at\":null}";
    var resp = ActivateResponse.Parse(json);

    Assert.NotNull(resp);
    Assert.True(resp!.Activated);
    Assert.Equal("id-1", resp.InstanceId);
    Assert.Null(resp.LicenseExpiresAt);
  }

  [Fact]
  public void ActivateResponse_deserializes_lease() {
    var json = "{\"activated\":true,\"instance_id\":\"id-1\",\"license_expires_at\":1234567890,\"lease\":{\"kid\":\"k1\",\"licenseKeyHash\":\"h\",\"instanceId\":\"id-1\",\"issuedAt\":100,\"expiresAt\":200,\"status\":\"active\",\"entitlements\":[\"pro\"],\"signature\":\"sig\"}}";
    var resp = ActivateResponse.Parse(json);

    Assert.NotNull(resp);
    Assert.NotNull(resp!.Lease);
    Assert.Equal("k1", resp.Lease!.Kid);
    Assert.Equal(1234567890L, resp.LicenseExpiresAt);
  }

  [Fact]
  public void ValidateResponse_deserializes_valid_and_lease() {
    var json = "{\"valid\":false,\"license_expires_at\":null}";
    var resp = ValidateResponse.Parse(json);

    Assert.NotNull(resp);
    Assert.False(resp!.Valid);
    Assert.Null(resp.Lease);
  }
}
