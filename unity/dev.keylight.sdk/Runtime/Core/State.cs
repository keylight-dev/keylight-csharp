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
    Invalid
  }
}
