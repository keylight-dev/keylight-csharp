using System.Security.Cryptography;
using System.Text;

namespace Keylight {
  /// <summary>
  /// The cross-SDK <c>machine_hash</c>: lets the server dedupe keyless devices
  /// by a true hardware identifier instead of a per-install random id. Only
  /// ever computed from a real hardware id — callers omit the field when
  /// there is none. Must match byte-for-byte across all Keylight SDKs
  /// (keylight-rust <c>machine.rs</c>, keylight-cpp <c>machine_id.hpp</c>).
  /// </summary>
  internal static class MachineId {
    internal static string Hash(string tenantId, string productId, string stableId) {
      var material = "keylight-keyless-machine-v1|" + tenantId + "|" + productId + "|" + stableId;
      using var sha = SHA256.Create();
      var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
      var sb = new StringBuilder(64);
      foreach (var b in digest) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
      return sb.ToString();
    }
  }
}
