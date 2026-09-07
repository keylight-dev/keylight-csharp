using Keylight;
using Keylight.Json;
using Xunit;

namespace Keylight.Tests {
  public class KeylessWireTests {
    [Fact]
    public void Keyless_request_serialises_the_contract_fields() {
      var req = new KeylessRequest { InstanceId = "ft-1", State = "trial", MachineHash = new string('a', 64), SdkVersion = "0.5.0", Platform = "macos" };
      var root = JsonCodec.Parse(req.ToJson())!;
      Assert.Equal("ft-1", root.Get("instance_id")!.AsString());
      Assert.Equal("trial", root.Get("state")!.AsString());
      Assert.Equal(new string('a', 64), root.Get("machine_hash")!.AsString());
      Assert.Equal("csharp", root.Get("sdk")!.AsString());
      Assert.Equal("0.5.0", root.Get("sdk_version")!.AsString());
    }

    [Fact]
    public void Keyless_request_omits_machine_hash_when_null() {
      var req = new KeylessRequest { InstanceId = "ft-1", State = "free_tier" };
      Assert.DoesNotContain("machine_hash", req.ToJson());
    }

    [Fact]
    public void Keyless_response_parses_received_settings_and_signature() {
      var json = "{\"received\":true,\"free_tier_enabled\":true,\"trial_duration_days\":14,\"kid\":\"k1\",\"issued_at\":1,\"expires_at\":2,\"signature\":\"c2ln\"}";
      var resp = KeylessResponse.Parse(json)!;
      Assert.True(resp.Received);
      Assert.Equal(14, resp.ConfigFields.TrialDurationDays);
      Assert.True(resp.ConfigFields.FreeTierEnabled);
      Assert.Equal("k1", resp.ConfigSignature!.Kid);
    }

    [Fact]
    public void Activate_and_validate_carry_machine_hash_and_omit_it_when_null() {
      var a = new ActivateRequest { LicenseKey = "K", InstanceName = "n", MachineHash = new string('b', 64) };
      Assert.Equal(new string('b', 64), JsonCodec.Parse(a.ToJson())!.Get("machine_hash")!.AsString());
      var v = new ValidateRequest { LicenseKey = "K", InstanceId = "i" };
      Assert.DoesNotContain("machine_hash", v.ToJson());
    }
  }
}
