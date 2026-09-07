using Keylight;
using Xunit;

namespace Keylight.Tests {

  /// <summary>
  /// The signature envelope on the wire: both routes that carry server-owned
  /// settings must surface a populated <see cref="ConfigSignature"/> when all
  /// four fields are present, and <c>null</c> — "unsigned", not "malformed" —
  /// when any one of them is missing.
  /// </summary>
  public class ConfigSignatureWireTests {

    private static readonly (string Name, string Json)[] Fields = {
      ("trial_duration_days", "14"),
      ("free_tier_enabled", "true"),
      ("kid", "\"k1\""),
      ("issued_at", "1700000000"),
      ("expires_at", "1700086400"),
      ("signature", "\"c2ln\""),
    };

    /// <summary>The full signed body, minus one named field when asked.</summary>
    private static string Body(string? without = null) {
      var parts = new System.Collections.Generic.List<string>();
      foreach (var (name, json) in Fields)
        if (name != without) parts.Add("\"" + name + "\":" + json);
      return "{" + string.Join(",", parts) + "}";
    }

    private static string Signed => Body();

    private static string Without(string field) {
      var body = Body(field);
      Assert.DoesNotContain(field, body);
      return body;
    }

    [Fact]
    public void ConfigResponse_parses_the_signature_envelope() {
      var resp = ConfigResponse.Parse(Signed);
      Assert.NotNull(resp);
      Assert.Equal(14, resp!.TrialDurationDays);
      Assert.True(resp.FreeTierEnabled);
      var sig = resp.ConfigSignature;
      Assert.NotNull(sig);
      Assert.Equal("k1", sig!.Kid);
      Assert.Equal(1700000000L, sig.IssuedAt);
      Assert.Equal(1700086400L, sig.ExpiresAt);
      Assert.Equal("c2ln", sig.Signature);
    }

    [Fact]
    public void ValidateResponse_parses_the_signature_envelope() {
      var resp = ValidateResponse.Parse("{\"valid\":false," + Signed.Substring(1));
      Assert.NotNull(resp);
      Assert.False(resp!.Valid);
      Assert.Equal(14, resp.TrialDurationDays);
      Assert.True(resp.FreeTierEnabled);
      var sig = resp.ConfigSignature;
      Assert.NotNull(sig);
      Assert.Equal("k1", sig!.Kid);
      Assert.Equal(1700000000L, sig.IssuedAt);
      Assert.Equal(1700086400L, sig.ExpiresAt);
      Assert.Equal("c2ln", sig.Signature);
    }

    [Theory]
    [InlineData("kid")]
    [InlineData("issued_at")]
    [InlineData("expires_at")]
    [InlineData("signature")]
    public void Missing_any_one_field_leaves_the_signature_null_on_both_routes(string field) {
      var body = Without(field);

      var cfg = ConfigResponse.Parse(body);
      Assert.NotNull(cfg);
      Assert.Null(cfg!.ConfigSignature);
      // The settings themselves still parse: unsigned is not malformed.
      Assert.Equal(14, cfg.TrialDurationDays);

      var val = ValidateResponse.Parse("{\"valid\":true," + body.Substring(1));
      Assert.NotNull(val);
      Assert.Null(val!.ConfigSignature);
      Assert.Equal(14, val.TrialDurationDays);
    }
  }
}
