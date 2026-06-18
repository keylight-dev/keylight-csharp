using Keylight;
using Xunit;

public class LeaseTests {
  [Fact]
  public void Canonical_sorts_entitlements_and_joins_with_pipe() {
    var lease = new Lease {
      Kid = "k1", LicenseKeyHash = "abc", InstanceId = "id1",
      IssuedAt = 100, ExpiresAt = 200, Status = "active",
      Entitlements = new[] { "pro", "beta" }, Signature = "sig"
    };
    Assert.Equal("v3|k1|abc|id1|100|200|active|beta,pro", LeasePayload.Canonical(lease));
  }

  [Fact]
  public void Canonical_empty_entitlements_is_trailing_empty() {
    var lease = new Lease {
      Kid = "k1", LicenseKeyHash = "h", InstanceId = "i",
      IssuedAt = 1, ExpiresAt = 2, Status = "active",
      Entitlements = new string[0], Signature = "s"
    };
    Assert.Equal("v3|k1|h|i|1|2|active|", LeasePayload.Canonical(lease));
  }
}
