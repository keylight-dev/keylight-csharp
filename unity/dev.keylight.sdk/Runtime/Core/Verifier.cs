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

    static byte[]? B64(string s) {
      if (s == null) return null;
      var t = new StringBuilder(s.Length);
      foreach (var c in s) { if (char.IsWhiteSpace(c)) continue; t.Append(c == '-' ? '+' : c == '_' ? '/' : c); }
      var n = t.ToString(); var pad = n.Length % 4; if (pad != 0) n += new string('=', 4 - pad);
      try { return Convert.FromBase64String(n); } catch { return null; }
    }
  }
}
