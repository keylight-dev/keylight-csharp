using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace Keylight {
  public sealed class Lease {
    [JsonPropertyName("kid")] public string Kid { get; set; } = "";
    [JsonPropertyName("licenseKeyHash")] public string LicenseKeyHash { get; set; } = "";
    [JsonPropertyName("instanceId")] public string InstanceId { get; set; } = "";
    [JsonPropertyName("issuedAt")] public long IssuedAt { get; set; }
    [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("entitlements")] public string[] Entitlements { get; set; } = Array.Empty<string>();
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
  }

  public static class LeasePayload {
    /// <summary>Exact UTF-8 preimage that was signed; entitlements sorted ascending (ordinal).</summary>
    public static string Canonical(Lease l) {
      var ents = (l.Entitlements ?? Array.Empty<string>()).OrderBy(e => e, StringComparer.Ordinal);
      return $"v3|{l.Kid}|{l.LicenseKeyHash}|{l.InstanceId}|{l.IssuedAt}|{l.ExpiresAt}|{l.Status}|{string.Join(",", ents)}";
    }
  }
}
