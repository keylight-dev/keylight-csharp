using System;
using System.Globalization;

namespace Keylight {

  /// <summary>
  /// The signature envelope that rides alongside server-owned product settings
  /// on <c>/config</c> and <c>/validate</c> — the two routes this SDK has.
  /// </summary>
  /// <remarks>
  /// All four fields arrive together or not at all — a worker that predates
  /// signing sends none of them. Absence therefore means "unsigned", not
  /// "malformed".
  /// </remarks>
  public sealed class ConfigSignature {
    public long IssuedAt { get; set; }
    public long ExpiresAt { get; set; }
    public string Kid { get; set; } = "";
    public string Signature { get; set; } = "";
  }

  /// <summary>The canonical preimage for a signed product config.</summary>
  /// <remarks>
  /// Frozen across every SDK. Once a verifying client is in the wild these bytes
  /// cannot change: every shipped client would reject valid configs, and it
  /// cannot be fixed from the server. <c>freeTierEnabled</c> is the literal
  /// <c>true</c>/<c>false</c>. Numbers are formatted with the invariant
  /// culture: a locale that groups digits or uses a non-ASCII minus sign would
  /// otherwise silently produce bytes the worker never signed.
  /// </remarks>
  public static class ConfigPayload {
    public static string Canonical(
        string kid, string tenantId, string productId,
        long issuedAt, long expiresAt, int trialDurationDays, bool freeTierEnabled) =>
      "cfg1|" + kid + "|" + tenantId + "|" + productId + "|"
        + issuedAt.ToString(CultureInfo.InvariantCulture) + "|"
        + expiresAt.ToString(CultureInfo.InvariantCulture) + "|"
        + trialDurationDays.ToString(CultureInfo.InvariantCulture) + "|"
        + (freeTierEnabled ? "true" : "false");
  }
}
