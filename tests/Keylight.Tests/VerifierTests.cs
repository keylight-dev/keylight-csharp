using System.Collections.Generic;
using Keylight;
using Xunit;

public class VerifierTests {
  [Fact]
  public void Unknown_kid_is_not_trusted() {
    var lease = new Lease { Kid = "kX", ExpiresAt = 10_000 };
    var r = Verifier.VerifyLease(lease, new Dictionary<string,string>(), nowSeconds: 0);
    Assert.False(r.KidKnown); Assert.False(r.SignatureValid); Assert.False(Verifier.IsTrusted(r));
  }
  [Fact]
  public void Expired_uses_300s_skew() {
    var lease = new Lease { Kid = "k1", ExpiresAt = 1000, Signature = "x" };
    var keys = new Dictionary<string,string> { ["k1"] = "AAAA" };
    Assert.False(Verifier.VerifyLease(lease, keys, 1299).Expired); // within skew
    Assert.True(Verifier.VerifyLease(lease, keys, 1301).Expired);  // beyond skew
  }
}
