using System;
using System.Threading;
using System.Threading.Tasks;

namespace Keylight {
  /// <summary>
  /// The Keylight client. Entry-point for all activation, validation, and
  /// entitlement queries. Thread-safe for concurrent reads of <see cref="State"/>
  /// and <see cref="HasEntitlement"/>; async methods should not be called
  /// concurrently on the same instance.
  /// </summary>
  public sealed class KeylightClient {
    private readonly KeylightConfig _config;
    private readonly ILeaseStore _store;
    private readonly IKeylightTransport _transport;

    // Clock seam: returns Unix seconds. Defaults to real wall clock.
    // Exposed as an internal constructor parameter so tests can inject a
    // deterministic clock without altering production behaviour.
    private readonly Func<long> _nowSeconds;

    // ─── in-memory cache ─────────────────────────────────────────────────────
    // Eliminates redundant _store.Load() + Ed25519 calls on read paths.
    // Written only from write paths (ActivateAsync, ValidateAsync, DeactivateAsync,
    // CheckOnLaunchAsync trial-start) which are not called concurrently per the
    // documented contract. Read paths only read these fields — no lock needed.
    private bool          _cachePopulated;      // false until first RefreshCache()
    private CachedState?  _cachedState;         // null when store is empty
    private VerifyResult? _cachedVerifyResult;  // null when no lease on disk

    private static long RealNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ─── constructors ────────────────────────────────────────────────────────

    /// <summary>
    /// Production constructor. Uses <see cref="FileLeaseStore"/> and
    /// <see cref="HttpClientTransport"/> built from <paramref name="config"/>
    /// when the optional parameters are omitted.
    /// </summary>
    public KeylightClient(
      KeylightConfig config,
      ILeaseStore? store = null,
      IKeylightTransport? transport = null)
      : this(config, store, transport, nowSeconds: null) { }

    /// <summary>
    /// Constructor with a clock seam for deterministic testing.
    /// In production code use the 3-parameter overload; this overload is
    /// intentionally public so test assemblies can inject a fake clock without
    /// requiring <c>InternalsVisibleTo</c>.
    /// </summary>
    public KeylightClient(
      KeylightConfig config,
      ILeaseStore? store,
      IKeylightTransport? transport,
      Func<long>? nowSeconds) {
      _config = config ?? throw new ArgumentNullException(nameof(config));
      _store = store ?? new FileLeaseStore();
      _transport = transport ?? new HttpClientTransport(
        config.BaseUrl, config.TenantId, config.ProductId, config.SdkKey);
      _nowSeconds = nowSeconds ?? RealNow;
    }

    // ─── public API — version ────────────────────────────────────────────────

    /// <summary>The SDK version string.</summary>
    public string SdkVersion => SdkInfo.Version;

    // ─── public API — state ──────────────────────────────────────────────────

    /// <summary>
    /// Synchronous state derived from the cached, signature-verified lease.
    /// Never throws; returns <see cref="KeylightState.Invalid"/> when no valid
    /// lease is present.
    /// </summary>
    public KeylightState State => ResolveState();

    /// <summary>
    /// Returns true if the cached trusted lease contains the given entitlement
    /// key. Only non-expired, signature-verified leases are consulted.
    /// </summary>
    public bool HasEntitlement(string key) {
      var lease = GetCachedTrustedLease();
      if (lease == null) return false;
      foreach (var e in lease.Entitlements)
        if (string.Equals(e, key, StringComparison.Ordinal)) return true;
      return false;
    }

    // ─── public API — async operations ──────────────────────────────────────

    /// <summary>
    /// Activates a license key on this device. On a successful server response
    /// the returned lease is verified; a tampered or untrusted lease throws
    /// <see cref="LeaseVerificationFailedException"/> before the state is updated.
    /// </summary>
    /// <exception cref="LeaseVerificationFailedException">
    ///   Server returned a lease that fails signature verification.
    /// </exception>
    /// <exception cref="ActivationException">
    ///   Server returned a non-success status or <c>activated=false</c>.
    /// </exception>
    public async Task ActivateAsync(string licenseKey, CancellationToken ct = default) {
      var req = new ActivateRequest {
        LicenseKey   = licenseKey,
        InstanceName = Device.DefaultInstanceName(),
        AppVersion   = _config.AppVersion,
        SdkVersion   = SdkInfo.Version,
        Platform     = Device.Platform
      };

      ActivateResponse resp;
      try {
        resp = await _transport.ActivateAsync(req, ct).ConfigureAwait(false);
      } catch (Exception ex) {
        throw new ActivationException(0, $"Activation request failed: {ex.Message}");
      }

      if (!resp.Activated)
        throw new ActivationException(0, "Activation failed: server returned activated=false");

      // Verify-or-reject before caching
      if (resp.Lease != null)
        VerifyOrReject(resp.Lease);

      var state = new CachedState {
        Lease      = resp.Lease,
        InstanceId = resp.InstanceId,
        FetchedAt  = _nowSeconds()
      };
      _store.Save(state);
      RefreshCache();
    }

    /// <summary>
    /// Validates the stored lease online. Updates the cache if the server
    /// returns a new lease.
    /// </summary>
    public async Task ValidateAsync(CancellationToken ct = default) {
      // Use cached state; prime if needed.
      if (!_cachePopulated) RefreshCache();
      var cached = _cachedState;
      var instanceId = cached?.InstanceId ?? "";

      var req = new ValidateRequest {
        InstanceId = instanceId,
        AppVersion = _config.AppVersion,
        SdkVersion = SdkInfo.Version,
        Platform   = Device.Platform
      };

      ValidateResponse resp;
      try {
        resp = await _transport.ValidateAsync(req, ct).ConfigureAwait(false);
      } catch {
        // Network failures are non-fatal for validate — the client stays in
        // whatever state the cache dictates.
        return;
      }

      if (resp.Lease != null) {
        // Verify-or-reject before persisting the updated lease
        VerifyOrReject(resp.Lease);
        var newState = new CachedState {
          Lease      = resp.Lease,
          InstanceId = cached?.InstanceId,
          FetchedAt  = _nowSeconds()
        };
        _store.Save(newState);
        RefreshCache();
      }
    }

    /// <summary>
    /// Refreshes the lease if it is stale (older than 5 minutes) or near
    /// expiry. A no-op if no license is stored.
    /// </summary>
    public async Task RefreshIfNeededAsync(CancellationToken ct = default) {
      // Use cached state to avoid a redundant _store.Load().
      // If cache is unpopulated, prime it now.
      if (!_cachePopulated) RefreshCache();
      var cached = _cachedState;
      if (cached == null) return;

      var now = _nowSeconds();
      var ageSeconds = now - cached.FetchedAt;

      // Debounce: skip if refreshed within the last 5 minutes
      if (ageSeconds < 300) return;

      // Near-expiry threshold: 24 h
      var lease = cached.Lease;
      bool nearExpiry = lease != null && (lease.ExpiresAt - now) < 86400;

      // Refresh if stale (>6h) or near expiry
      if (ageSeconds >= 21600 || nearExpiry)
        await ValidateAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deactivates this device. Clears the local cache regardless of whether
    /// the server call succeeds, mirroring JS/Rust parity.
    /// </summary>
    public async Task DeactivateAsync(CancellationToken ct = default) {
      // Use cached state; prime if needed.
      if (!_cachePopulated) RefreshCache();
      var cached = _cachedState;
      var instanceId = cached?.InstanceId;

      if (!string.IsNullOrEmpty(instanceId)) {
        try {
          await _transport.DeactivateAsync(new DeactivateRequest { InstanceId = instanceId! }, ct)
            .ConfigureAwait(false);
        } catch {
          // Swallow network errors — cache is cleared regardless.
        }
      }

      _store.Clear();
      RefreshCache();
    }

    /// <summary>
    /// Called on app launch: refreshes the lease if a license is stored and
    /// the cached lease is stale or near expiry. Also auto-starts the trial
    /// (persists <c>TrialStartedAt</c>) on the first launch when
    /// <see cref="KeylightConfig.TrialDurationDays"/> is configured and no
    /// trusted active license is present.
    /// </summary>
    public async Task CheckOnLaunchAsync(CancellationToken ct = default) {
      // Prime the cache before any reads below.
      RefreshCache();

      if (_cachedState != null)
        await RefreshIfNeededAsync(ct).ConfigureAwait(false);
      // Note: RefreshIfNeededAsync → ValidateAsync may call RefreshCache()
      // internally (via _store.Save → RefreshCache), so _cachedState reflects
      // the latest state after it returns.

      // Auto-start trial: once, idempotent, only when no trusted active license
      // and TrialDurationDays is configured.
      if (_config.TrialDurationDays.HasValue && _config.TrialDurationDays.Value > 0) {
        var trusted = GetCachedTrustedLease();
        bool hasActiveLicense = trusted != null && trusted.Status == "active";

        if (!hasActiveLicense) {
          // Use the cached state; fall back to a fresh CachedState for first launch.
          var current = _cachedState ?? new CachedState { FetchedAt = _nowSeconds() };
          if (!current.TrialStartedAt.HasValue) {
            current.TrialStartedAt = _nowSeconds();
            _store.Save(current);
            RefreshCache();
          }
        }
      }
    }

    // ─── public API — sync wrappers ──────────────────────────────────────────

    // Deadlock-safe because every await in the async path uses ConfigureAwait(false).

    /// <summary>Synchronous wrapper for <see cref="ActivateAsync"/>.</summary>
    public void Activate(string licenseKey)
      => ActivateAsync(licenseKey).GetAwaiter().GetResult();

    /// <summary>Synchronous wrapper for <see cref="ValidateAsync"/>.</summary>
    public void Validate()
      => ValidateAsync().GetAwaiter().GetResult();

    /// <summary>Synchronous wrapper for <see cref="DeactivateAsync"/>.</summary>
    public void Deactivate()
      => DeactivateAsync().GetAwaiter().GetResult();

    // ─── cache management ────────────────────────────────────────────────────

    /// <summary>
    /// Loads from disk once and caches both the <see cref="CachedState"/> and
    /// the <see cref="VerifyResult"/> (signature verification result). Call this
    /// after every write path that mutates the store so read paths always see
    /// current state without hitting disk or re-running Ed25519.
    /// </summary>
    private void RefreshCache() {
      var loaded = _store.Load();
      _cachedState = loaded;
      _cachedVerifyResult = (loaded?.Lease != null)
        ? Verifier.VerifyLease(loaded.Lease, _config.TrustedKeys, _nowSeconds(), Verifier.SkewSeconds)
        : (VerifyResult?)null;
      _cachePopulated = true;
    }

    // ─── private helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the cached lease only if it passes signature verification AND
    /// is within the offline grace window AND is not expired. This is the
    /// "gated" lease used by HasEntitlement.
    /// </summary>
    private Lease? GetCachedTrustedLease() {
      // Ensure cache is populated (first read after construction or after a
      // write path has not yet called RefreshCache — defensive guard).
      if (!_cachePopulated) RefreshCache();

      if (_config.MaxOfflineDays > 0) {
        if (_cachedState == null) return null;
        var offlineSeconds = _nowSeconds() - _cachedState.FetchedAt;
        if (offlineSeconds > (long)_config.MaxOfflineDays * 86400) return null;
      }

      var raw = _cachedState?.Lease;
      if (raw == null) return null;

      // Reuse cached KidKnown + SignatureValid; recompute Expired fresh.
      var r = _cachedVerifyResult!.Value;
      bool expired = _nowSeconds() > raw.ExpiresAt + Verifier.SkewSeconds;
      return (Verifier.IsTrusted(r) && !expired && raw.Status != "expired") ? raw : null;
    }

    /// <summary>
    /// Resolves <see cref="KeylightState"/> from the raw cached lease (no
    /// offline-grace gating for the status read — mirrors JS state() logic).
    /// </summary>
    private KeylightState ResolveState() {
      // Ensure cache is populated.
      if (!_cachePopulated) RefreshCache();

      var rawLease = _cachedState?.Lease;
      if (rawLease == null) return CheckTrialOrInvalid();

      // Reuse cached KidKnown + SignatureValid; recompute Expired fresh.
      var r = _cachedVerifyResult!.Value;
      if (!Verifier.IsTrusted(r)) return CheckTrialOrInvalid();

      bool expired = _nowSeconds() > rawLease.ExpiresAt + Verifier.SkewSeconds;

      // Trusted lease: resolve by status
      switch (rawLease.Status) {
        case "active":
          if (!expired) return KeylightState.Licensed;
          // Stale active lease: fall through to Expired
          return KeylightState.Expired;
        case "expired":
          return KeylightState.Expired;
        case "fallback":
          // Limited state — map to Expired (C# enum has no Limited)
          return KeylightState.Expired;
        default:
          return KeylightState.Expired;
      }
    }

    private KeylightState CheckTrialOrInvalid() {
      if (_config.TrialDurationDays.HasValue && _config.TrialDurationDays.Value > 0) {
        // _cachedState is already populated by the caller (ResolveState or
        // GetCachedTrustedLease) so no additional _store.Load() is needed.
        if (_cachedState?.TrialStartedAt.HasValue == true) {
          var trialStart = _cachedState.TrialStartedAt.Value;
          var trialEndSeconds = trialStart + (long)_config.TrialDurationDays.Value * 86400L;
          return _nowSeconds() < trialEndSeconds
            ? KeylightState.Trial
            : KeylightState.Expired;
        }
      }
      return KeylightState.Invalid;
    }

    /// <summary>
    /// Throws <see cref="LeaseVerificationFailedException"/> if the lease is
    /// not trusted (kid unknown or signature invalid). Does NOT check expiry —
    /// expiry affects State, not whether we accept the lease from the server.
    /// </summary>
    private void VerifyOrReject(Lease lease) {
      var r = Verifier.VerifyLease(lease, _config.TrustedKeys, _nowSeconds(), Verifier.SkewSeconds);
      if (!Verifier.IsTrusted(r))
        throw new LeaseVerificationFailedException();
    }
  }
}
