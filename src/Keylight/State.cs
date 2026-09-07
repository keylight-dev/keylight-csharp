namespace Keylight {
  /// <summary>
  /// The resolved license state of the Keylight client. Derived synchronously
  /// from the cached, signature-verified lease.
  /// Mirrors Swift <c>LicenseState</c> and Rust <c>LicenseState</c>.
  /// </summary>
  public enum KeylightState {
    /// <summary>Active lease, trusted signature, within grace window.</summary>
    Licensed,
    /// <summary>Within the trial window (no active license).</summary>
    Trial,
    /// <summary>Lease expired beyond the offline grace window.</summary>
    Expired,
    /// <summary>No valid lease and no trial in progress.</summary>
    Invalid,
    /// <summary>No license and no running trial, but the product offers a free
    /// tier. Appended after <see cref="Invalid"/> so persisted integer values
    /// keep their meaning — add new members at the END.</summary>
    FreeTier,
    /// <summary>Trusted lease with server status <c>fallback</c>: the server
    /// could not mint a full lease, so the app runs degraded rather than locked.
    /// Appended last for the same reason.</summary>
    Limited
  }

  /// <summary>What an unlicensed device reports on the keyless beacon.</summary>
  public enum KeylessState { Trial, FreeTier, Expired }

  internal static class KeylessStateWire {
    internal static string Of(KeylessState s) => s switch {
      KeylessState.Trial => "trial",
      KeylessState.FreeTier => "free_tier",
      _ => "expired"
    };

    /// <summary>The beacon state a resolved license state calls for, or null.
    /// <c>Licensed</c> and <c>Limited</c> report liveness through validate;
    /// <c>Invalid</c> is a denial, not a device to count.</summary>
    internal static KeylessState? For(KeylightState s) => s switch {
      KeylightState.Trial => KeylessState.Trial,
      KeylightState.FreeTier => KeylessState.FreeTier,
      KeylightState.Expired => KeylessState.Expired,
      _ => null
    };
  }
}
