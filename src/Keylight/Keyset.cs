using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Keylight {
  /// <summary>
  /// The tenant's public keyset, fetched from
  /// <c>{baseUrl}/{tenantId}/.well-known/keylight-keys</c>.
  /// Mirrors the TypeScript <c>parseKeyset</c> semantics: any malformed entry
  /// causes the entire parse to return null rather than silently skipping it.
  /// </summary>
  public sealed class Keyset {
    /// <summary>The kid of the primary (current signing) key.</summary>
    public string PrimaryKid { get; }

    /// <summary>Map of kid → base64-encoded Ed25519 public key.</summary>
    public IReadOnlyDictionary<string, string> Keys { get; }

    private Keyset(string primaryKid, IReadOnlyDictionary<string, string> keys) {
      PrimaryKid = primaryKid;
      Keys = keys;
    }

    /// <summary>
    /// Parse a keyset JSON string.
    /// Returns null if <c>primary_kid</c> is missing/not-a-string, <c>keys</c>
    /// is not an array, or any element is missing <c>kid</c> or <c>public_key</c>.
    /// </summary>
    public static Keyset? Parse(string json) {
      if (string.IsNullOrEmpty(json)) return null;
      try {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // primary_kid must be a string
        if (!root.TryGetProperty("primary_kid", out var primaryKidEl) ||
            primaryKidEl.ValueKind != JsonValueKind.String) {
          return null;
        }
        var primaryKid = primaryKidEl.GetString();
        if (primaryKid == null) return null;

        // keys must be an array
        if (!root.TryGetProperty("keys", out var keysEl) ||
            keysEl.ValueKind != JsonValueKind.Array) {
          return null;
        }

        var keys = new Dictionary<string, string>();
        foreach (var entry in keysEl.EnumerateArray()) {
          // kid must be a string
          if (!entry.TryGetProperty("kid", out var kidEl) ||
              kidEl.ValueKind != JsonValueKind.String) {
            return null;
          }
          var kid = kidEl.GetString();
          if (kid == null) return null;

          // public_key must be a string
          if (!entry.TryGetProperty("public_key", out var pkEl) ||
              pkEl.ValueKind != JsonValueKind.String) {
            return null;
          }
          var publicKey = pkEl.GetString();
          if (publicKey == null) return null;

          keys[kid] = publicKey;
        }

        return new Keyset(primaryKid, keys);
      } catch {
        return null;
      }
    }

    /// <summary>
    /// Fetch the keyset from <c>{baseUrl}/{tenantId}/.well-known/keylight-keys</c>.
    /// Returns null on any non-200 response or parse failure. No auth header.
    /// </summary>
    public static async Task<Keyset?> FetchAsync(
      HttpClient http,
      string baseUrl,
      string tenantId,
      CancellationToken ct = default) {
      var url = $"{baseUrl.TrimEnd('/')}/{tenantId}/.well-known/keylight-keys";
      HttpResponseMessage response;
      try {
        response = await http.GetAsync(url, ct).ConfigureAwait(false);
      } catch {
        return null;
      }

      if ((int)response.StatusCode != 200) return null;

#if NETSTANDARD2_0
      var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#else
      var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
#endif
      return Parse(body);
    }
  }
}
