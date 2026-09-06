using System;
using System.Collections.Generic;
using System.Text;
using Keylight.Crypto;

namespace Keylight {
  public struct VerifyResult { public bool KidKnown; public bool SignatureValid; public bool Expired; }

  public static class Verifier {
    public const int SkewSeconds = 300;
    public static bool IsTrusted(VerifyResult r) => r.KidKnown && r.SignatureValid;

    public static VerifyResult VerifyLease(Lease lease, IReadOnlyDictionary<string,string> trustedKeys,
                                           long nowSeconds, int skewSeconds = SkewSeconds) {
      bool expired = nowSeconds > lease.ExpiresAt + skewSeconds;
      if (!trustedKeys.TryGetValue(lease.Kid, out var pubB64))
        return new VerifyResult { KidKnown = false, SignatureValid = false, Expired = expired };
      bool sigValid = false;
      try {
        var pk = B64(pubB64); var sig = B64(lease.Signature);
        if (pk != null && pk.Length == 32 && sig != null) {
          var msg = Encoding.UTF8.GetBytes(LeasePayload.Canonical(lease));
          sigValid = Ed25519.Verify(sig, msg, pk);
        }
      } catch { sigValid = false; }
      return new VerifyResult { KidKnown = true, SignatureValid = sigValid, Expired = expired };
    }

    /// <summary>
    /// Verify the Ed25519 signature over a set of server-owned product settings.
    /// </summary>
    /// <remarks>
    /// <paramref name="tenantId"/> and <paramref name="productId"/> come from the
    /// caller's own configuration, never from the body — that is what makes a
    /// config signed for another product fail rather than validate against its
    /// own claim.
    ///
    /// Freshness applies to the wire, not to the cache: a response fetched
    /// outside its own window is rejected here, while a config already cached
    /// stays usable past that window, because expiring it would cut an offline
    /// user's trial short for no security gain.
    /// </remarks>
    public static bool VerifyConfig(
        ProductConfigFields fields, ConfigSignature signature,
        string tenantId, string productId,
        IReadOnlyDictionary<string, string> trustedKeys,
        long nowSeconds, int skewSeconds = SkewSeconds) {
      if (fields == null || signature == null) return false;
      if (string.IsNullOrEmpty(signature.Signature)) return false;

      if (nowSeconds + skewSeconds < signature.IssuedAt) return false;
      if (signature.ExpiresAt + skewSeconds < nowSeconds) return false;

      // The worker signs a complete pair or sends the response unsigned, so a
      // partial config was never signable and must not be treated as if it were.
      if (!fields.TrialDurationDays.HasValue || !fields.FreeTierEnabled.HasValue) return false;

      if (!trustedKeys.TryGetValue(signature.Kid, out var pubB64)) return false;

      try {
        var pk = B64(pubB64);
        var sig = B64(signature.Signature);
        if (pk == null || pk.Length != 32 || sig == null) return false;
        var msg = Encoding.UTF8.GetBytes(ConfigPayload.Canonical(
          signature.Kid, tenantId, productId,
          signature.IssuedAt, signature.ExpiresAt,
          fields.TrialDurationDays.Value, fields.FreeTierEnabled.Value));
        return Ed25519.Verify(sig, msg, pk);
      } catch {
        return false;
      }
    }

    static byte[]? B64(string s) {
      if (s == null) return null;
      var t = new StringBuilder(s.Length);
      foreach (var c in s) { if (char.IsWhiteSpace(c)) continue; t.Append(c == '-' ? '+' : c == '_' ? '/' : c); }
      var n = t.ToString(); var pad = n.Length % 4; if (pad != 0) n += new string('=', 4 - pad);
      try { return Convert.FromBase64String(n); } catch { return null; }
    }
  }
}
