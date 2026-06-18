using System;
using System.Linq;

namespace Keylight {
  public sealed class Lease {
    public string Kid { get; set; } = "";
    public string LicenseKeyHash { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public long IssuedAt { get; set; }
    public long ExpiresAt { get; set; }
    public string Status { get; set; } = "";
    public string[] Entitlements { get; set; } = Array.Empty<string>();
    public string Signature { get; set; } = "";
  }

  public static class LeasePayload {
    /// <summary>Exact UTF-8 preimage that was signed; entitlements sorted ascending (ordinal).</summary>
    public static string Canonical(Lease l) {
      var ents = (l.Entitlements ?? Array.Empty<string>()).OrderBy(e => e, StringComparer.Ordinal);
      return $"v3|{l.Kid}|{l.LicenseKeyHash}|{l.InstanceId}|{l.IssuedAt}|{l.ExpiresAt}|{l.Status}|{string.Join(",", ents)}";
    }
  }
}
