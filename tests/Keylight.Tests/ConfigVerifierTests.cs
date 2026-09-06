using System.Collections.Generic;
using Keylight;
using Xunit;

/// <summary>
/// Ed25519 verification of server-owned product settings.
///
/// The payload format is frozen across every SDK — see section 4 of
/// <c>keylight-cpp/docs/superpowers/specs/2026-09-05-trial-parity-handoff.md</c>:
///
///   cfg1|{kid}|{tenantId}|{productId}|{issuedAt}|{expiresAt}|{trialDurationDays}|{freeTierEnabled}
///
/// Once a verifying client is in the wild these bytes cannot change, so the
/// first test is a golden vector captured from the live worker rather than a
/// fixture this suite generated for itself. A fixture only proves this file
/// agrees with itself; the golden vector proves it agrees with production — and
/// with the Swift SDK, which pins the same bytes.
/// </summary>
public class ConfigVerifierTests {

  // Captured from GET https://api.keylight.dev/anotheragence/getbarry/config on
  // 2026-09-06, worker version 717cfb7c. The public key is what
  // GET /anotheragence/.well-known/keylight-keys serves for kid k1.
  private const string TenantId = "anotheragence";
  private const string ProductId = "getbarry";
  private const string Kid = "k1";
  private const string PublicKey = "wPOiRNiP2hbc0O4UCAuO6FRRLKp4YvGtf8V27xnPzNY=";
  private const string Signature =
    "WbyLOjmB7jtA3Ny9Qon/uJtXpZx61/Vx+U7OsQpSD17xkem5QrYwvSQOmRLw7J6Ozhgr8r2bptQ/UhiDRUZ7DA==";
  private const long IssuedAt = 1788695996;
  private const long ExpiresAt = 1788782396;
  private const int TrialDays = 1;
  private const bool FreeTier = true;

  /// An instant inside the signature window, so freshness passes deterministically
  /// forever rather than for one day in 2026.
  private const long InsideWindow = 1788700000;

  private static IReadOnlyDictionary<string, string> Trusted(string kid = Kid) =>
    new Dictionary<string, string> { [kid] = PublicKey };

  private static ProductConfigFields Fields(int trialDays = TrialDays, bool? freeTier = FreeTier) =>
    new ProductConfigFields { TrialDurationDays = trialDays, FreeTierEnabled = freeTier };

  private static ConfigSignature Sig(string signature = Signature) =>
    new ConfigSignature {
      IssuedAt = IssuedAt, ExpiresAt = ExpiresAt, Kid = Kid, Signature = signature
    };

  private static bool Verify(
      ProductConfigFields? fields = null,
      ConfigSignature? signature = null,
      string tenantId = TenantId,
      string productId = ProductId,
      IReadOnlyDictionary<string, string>? trusted = null,
      long now = InsideWindow) =>
    Verifier.VerifyConfig(
      fields ?? Fields(), signature ?? Sig(), tenantId, productId, trusted ?? Trusted(), now);

  [Fact]
  public void Verifies_a_real_signature_from_the_live_worker() {
    Assert.True(Verify(), "C# disagrees with the bytes the deployed worker signs");
  }

  // Rule 1: tenant and product come from local config, not the body.

  [Fact]
  public void Rejects_a_config_signed_for_a_different_product() {
    Assert.False(Verify(productId: "someotherapp"));
  }

  [Fact]
  public void Rejects_a_config_signed_for_a_different_tenant() {
    Assert.False(Verify(tenantId: "someothertenant"));
  }

  [Fact]
  public void Rejects_a_kid_missing_from_the_trusted_keyset() {
    Assert.False(Verify(trusted: Trusted("k2")));
  }

  [Fact]
  public void Rejects_tampered_values() {
    Assert.False(Verify(fields: Fields(trialDays: 365)), "the whole point: a longer trial must not verify");
  }

  [Fact]
  public void Rejects_an_empty_signature() {
    Assert.False(Verify(signature: Sig(signature: "")));
  }

  // Rule 3: freshness applies to the wire.

  [Fact]
  public void Rejects_a_response_from_before_its_issued_at() {
    Assert.False(Verify(now: IssuedAt - Verifier.SkewSeconds - 1));
  }

  [Fact]
  public void Rejects_a_response_past_its_expiry() {
    Assert.False(Verify(now: ExpiresAt + Verifier.SkewSeconds + 1));
  }

  [Fact]
  public void Accepts_clock_drift_within_the_skew_tolerance() {
    Assert.True(Verify(now: ExpiresAt + Verifier.SkewSeconds - 1));
    Assert.True(Verify(now: IssuedAt - Verifier.SkewSeconds + 1));
  }

  /// The worker never signs a partial config, so one cannot be reconstructed.
  [Fact]
  public void Rejects_a_partial_config() {
    Assert.False(Verify(fields: Fields(freeTier: null)));
  }
}
