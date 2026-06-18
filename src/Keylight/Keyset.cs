using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Keylight.Json;

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
        var root = JsonCodec.Parse(json);
        if (root == null) return null;

        // primary_kid must be a string
        var primaryKidVal = root.Get("primary_kid");
        if (primaryKidVal == null || primaryKidVal.IsNull) return null;
        var primaryKid = primaryKidVal.AsString();
        if (primaryKid == null) return null;

        // keys must be an array
        var keysVal = root.Get("keys");
        if (keysVal == null || keysVal.IsNull) return null;
        var keysArr = keysVal.AsArray();
        if (keysArr == null) return null;

        var keys = new Dictionary<string, string>();
        foreach (var entry in keysArr) {
          // kid must be a string
          var kidVal = entry.Get("kid");
          if (kidVal == null || kidVal.IsNull) return null;
          var kid = kidVal.AsString();
          if (kid == null) return null;

          // public_key must be a string
          var pkVal = entry.Get("public_key");
          if (pkVal == null || pkVal.IsNull) return null;
          var publicKey = pkVal.AsString();
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
