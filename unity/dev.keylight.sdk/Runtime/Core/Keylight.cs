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

    // ─── active-revalidate debounce ──────────────────────────────────────────
    // Monotonic-millisecond stamp of the last ActiveRevalidateAsync attempt.
    // Held in memory ONLY — never written to the store — so a process restart is
    // free to revalidate immediately.
    //
    // Deliberately NOT on the wall clock. The debounce suppresses revalidation,
    // so a clock that moves backwards suppresses revocation enforcement for the
    // size of the jump — and on a licensing SDK, moving the clock backwards is
    // an adversarial move we already defend against elsewhere, not just an NTP
    // correction. A monotonic source cannot be steered this way.
    private long? _lastActiveRevalidateAt;

    private const long ActiveRevalidateDebounceMillis = 60_000;

    // Monotonic source for the active-revalidate debounce. Exposed as an
    // internal constructor parameter for the same reason as _nowSeconds: the
    // 60s window is untestable otherwise.
    private readonly Func<long> _monotonicMillis;

    private static long RealNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>
    /// Process-lifetime monotonic clock. Unaffected by wall-clock changes, NTP
    /// steps, and timezone/DST transitions.
    ///
    /// A Stopwatch rather than Environment.TickCount64 because this assembly
    /// still targets netstandard2.0, where TickCount64 does not exist; and
    /// rather than scaling Stopwatch.GetTimestamp() by hand, which overflows a
    /// long once uptime gets large on a nanosecond-frequency timer.
    /// </summary>
    private static readonly System.Diagnostics.Stopwatch MonotonicClock =
      System.Diagnostics.Stopwatch.StartNew();

    private static long RealMonotonicMillis() => MonotonicClock.ElapsedMilliseconds;

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
      Func<long>? nowSeconds,
      Func<long>? monotonicMillis = null) {
      _config = config ?? throw new ArgumentNullException(nameof(config));
      _store = store ?? new FileLeaseStore();
      _transport = transport ?? new HttpClientTransport(
        config.BaseUrl, config.TenantId, config.ProductId, config.SdkKey);
      _nowSeconds = nowSeconds ?? RealNow;
      _monotonicMillis = monotonicMillis ?? RealMonotonicMillis;
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
    /// True when this device activated under an SDK build that did not persist
    /// the license key, leaving it unable to check in with the server.
    ///
    /// <para><b>Why this exists.</b> /validate and /deactivate both require
    /// <c>license_key</c> on the wire, and the key is not recoverable from local
    /// state — the cached lease carries only <c>LicenseKeyHash</c>. Such an
    /// install can never refresh its lease, so once the cached lease passes its
    /// own 7-day expiry <see cref="State"/> drops to
    /// <see cref="KeylightState.Expired"/> and stays there. That strands a
    /// PAYING customer, and it also delays revocation by up to the same 7 days.
    /// Only re-running <see cref="ActivateAsync"/> with the key can recover
    /// it.</para>
    ///
    /// <para><b>What to do with it.</b> Prompt for the license key and call
    /// <see cref="ActivateAsync"/>. Check it at launch, before the lease lapses,
    /// so recovery is a prompt rather than a lockout. Trial-only devices —
    /// which never activated and are not stranded — report false.</para>
    /// </summary>
    public bool NeedsReactivation {
      get {
        var cached = Cached();
        if (cached == null) return false;
        if (!string.IsNullOrEmpty(cached.LicenseKey)) return false;
        // Something was activated here (an instance id or a lease) but the key
        // that produced it was never stored.
        return !string.IsNullOrEmpty(cached.InstanceId) || cached.Lease != null;
      }
    }

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
      // Client-side key-format guard: fail fast before any network call.
      if (!string.IsNullOrEmpty(_config.KeyPrefix) &&
          !licenseKey.StartsWith(_config.KeyPrefix, StringComparison.Ordinal))
        throw new ActivationException(0, $"License key does not match expected prefix '{_config.KeyPrefix}'.");

      var req = new ActivateRequest {
        LicenseKey   = licenseKey,
        InstanceName = Device.DefaultInstanceName,
        AppVersion   = _config.AppVersion,
        SdkVersion   = SdkInfo.Version,
        Platform     = _config.Platform ?? Device.Platform,
        CpuCores     = Device.CpuCores,
        OsVersion    = Device.OsVersionValue,
        Arch         = Device.Arch,
        Memory       = Device.Memory,
        SdkTrialDurationDays = _config.TrialDurationDays
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
        // Required on every subsequent /validate and /deactivate.
        LicenseKey = licenseKey,
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
      var cached = Cached();
      var instanceId = cached?.InstanceId ?? "";
      var licenseKey = cached?.LicenseKey;

      // The worker requires license_key; sending an empty one is a guaranteed
      // 400. Installs that activated before the key was persisted, and
      // trial-only devices, have none — skip the call and keep last-known-good
      // rather than burning a round-trip on a certain rejection.
      //
      // This is silent by design (a hot path must not throw), so the stranded
      // case is surfaced separately via NeedsReactivation — see that property
      // for why such an install lapses to Expired within the lease's 7 days.
      if (string.IsNullOrEmpty(licenseKey)) return;

      var req = new ValidateRequest {
        LicenseKey = licenseKey!,
        InstanceId = instanceId,
        AppVersion = _config.AppVersion,
        SdkVersion = SdkInfo.Version,
        Platform   = _config.Platform ?? Device.Platform,
        CpuCores   = Device.CpuCores,
        OsVersion  = Device.OsVersionValue,
        Arch       = Device.Arch,
        Memory     = Device.Memory,
        SdkTrialDurationDays = _config.TrialDurationDays
      };

      ValidateResponse resp;
      try {
        resp = await _transport.ValidateAsync(req, ct).ConfigureAwait(false);
      } catch (ActivationException ex) when (ex.StatusCode == 422 && ex.Body != null) {
        // The worker's definitive-rejection shape (revoked license, deactivated/
        // unknown instance) comes back as HTTP 422 with a JSON body and no
        // top-level `valid` field — HttpClientTransport surfaces that as a
        // thrown ActivationException rather than a parsed ValidateResponse.
        // Treat it as a decodable response (mirrors the JS SDK's `decodable4xx`
        // handling of /validate's 422) instead of a transport failure, so the
        // lease-present / no-lease logic below actually runs for a real revoke
        // instead of hitting the network no-op path and keeping a stale lease.
        var decoded = ValidateResponse.Parse(ex.Body);
        if (decoded == null) return; // Undecodable body: fail safe, keep last-known-good.
        resp = decoded;
      } catch {
        // Genuine transport/network failures (no HTTP response at all — timeout,
        // DNS failure, connection refused, and non-422 server errors like 429/5xx)
        // are the ONLY no-op path — the client stays in whatever state the cache
        // dictates (last-known-good).
        return;
      }

      if (resp.Lease != null) {
        // Verify-or-reject before persisting the updated lease
        VerifyOrReject(resp.Lease);
        var newState = new CachedState {
          Lease          = resp.Lease,
          InstanceId     = cached?.InstanceId,
          // Carry the key forward: every future check-in needs it on the wire.
          LicenseKey     = cached?.LicenseKey,
          FetchedAt      = _nowSeconds(),
          TrialStartedAt = cached?.TrialStartedAt,
          // Carry the server-owned settings forward. This type is rebuilt from
          // scratch rather than mutated, so a field omitted here is a field
          // silently reset to the seed on the next validate.
          ProductTrialDurationDays = cached?.ProductTrialDurationDays,
          ProductFreeTierEnabled   = cached?.ProductFreeTierEnabled
        };
        _store.Save(newState);
        RefreshCache();
      } else if (!resp.Valid) {
        // Definitive rejection with no lease (revoked / deactivated instance /
        // unknown license): the server responded but refused to vouch for us.
        // This must NOT be treated as a no-op — previously `resp.Valid` was
        // parsed but never read, so a revoked license kept being trusted
        // until its cached lease's own ExpiresAt. Clear the trusted lease so
        // the next State/HasEntitlement read resolves to Invalid (or, if a
        // trial is configured and still running, falls back to Trial —
        // mirroring the same precedence DeactivateAsync already uses).
        var newState = new CachedState {
          Lease          = null,
          InstanceId     = cached?.InstanceId,
          // Carry the key forward: every future check-in needs it on the wire.
          LicenseKey     = cached?.LicenseKey,
          FetchedAt      = _nowSeconds(),
          TrialStartedAt = cached?.TrialStartedAt,
          // Carry the server-owned settings forward. This type is rebuilt from
          // scratch rather than mutated, so a field omitted here is a field
          // silently reset to the seed on the next validate.
          ProductTrialDurationDays = cached?.ProductTrialDurationDays,
          ProductFreeTierEnabled   = cached?.ProductFreeTierEnabled
        };
        _store.Save(newState);
        RefreshCache();
      }
      // resp.Valid == true && resp.Lease == null: the server confirmed
      // validity without sending a refreshed lease. Nothing to persist —
      // the existing cached lease remains authoritative.

      // The server-owned settings ride on this response. Absorbed after the
      // branches above so it merges into whatever they just wrote, and
      // unconditionally: the settings are valid regardless of whether the
      // licence itself validated.
      AbsorbConfigFields(resp.ConfigFields, resp.ConfigSignature);
    }

    /// <summary>
    /// Refreshes the lease if it is stale (older than 5 minutes) or near
    /// expiry. A no-op if no license is stored.
    /// </summary>
    public async Task RefreshIfNeededAsync(CancellationToken ct = default) {
      var cached = Cached();
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
    /// Forces an immediate server revalidation on active use (app foreground,
    /// popover opened, window focused), debounced to 60 seconds. Unlike
    /// <see cref="RefreshIfNeededAsync"/> this is never skipped for staleness
    /// reasons, so a dashboard revoke takes effect within minutes of the user
    /// touching the app instead of lagging until the next launch
    /// (<see cref="CheckOnLaunchAsync"/>).
    ///
    /// A definitive server rejection (<c>valid:false</c>, including the
    /// worker's HTTP 422 revoke shape) downgrades the cached state
    /// immediately. A transient/thrown failure leaves the cached state
    /// untouched — a live session is never downgraded on a network blip.
    /// Never throws.
    ///
    /// A no-op when nothing is stored, or when the stored state has never been
    /// activated (trial-only: no instance id and no lease) — there is nothing
    /// for the server to revalidate.
    ///
    /// The debounce is held in memory only and is deliberately not persisted:
    /// a fresh process revalidates on its first call.
    /// </summary>
    public async Task ActiveRevalidateAsync(CancellationToken ct = default) {
      var cached = Cached();
      if (cached == null) return;
      // Never activated (trial-only: no instance id and no lease) — there is
      // nothing for the server to revalidate.
      if (string.IsNullOrEmpty(cached.InstanceId) && cached.Lease == null) return;

      var now = _monotonicMillis();
      if (_lastActiveRevalidateAt.HasValue &&
          (now - _lastActiveRevalidateAt.Value) < ActiveRevalidateDebounceMillis)
        return;

      // Stamp before the call (mirrors the Swift SDK): a failing attempt still
      // consumes the window, so a flapping network can't be hammered.
      _lastActiveRevalidateAt = now;

      try {
        await ValidateAsync(ct).ConfigureAwait(false);
      } catch {
        // Transient — never downgrade a live session on a blip. ValidateAsync
        // already swallows genuine transport failures; this guard additionally
        // absorbs a LeaseVerificationFailedException from a server payload we
        // refuse to trust, because active-use triggers fire from UI code paths
        // where an escaping exception would crash the host app.
      }
    }

    /// <summary>
    /// Polls the server after a purchase or plan change until the stored
    /// license's entitlements or resolved <see cref="State"/> differ from what
    /// they were when the call started. Returns <c>true</c> as soon as either
    /// changes, <c>false</c> on timeout, cancellation, or when no license is
    /// stored (in which case nothing is sent). Never throws.
    /// </summary>
    /// <remarks>
    /// The upgrade path is: the user pays in a browser, the dashboard updates
    /// the license, and the app has no way to know except by asking. Rather
    /// than make every host write the same polling loop, this one is shared
    /// across the SDKs (Swift, JS, Rust) with identical semantics:
    ///
    /// <list type="bullet">
    ///   <item>Defaults: 30 s <paramref name="timeout"/>, 2 s <paramref name="pollInterval"/>;
    ///     the interval is floored at 100 ms so a mistaken zero cannot hammer the worker.</item>
    ///   <item>A definitive rejection that changes state (a revoke landing
    ///     mid-poll) counts as a change — the caller asked "did anything
    ///     happen", not "did the good thing happen".</item>
    ///   <item>Transient validate failures are swallowed and polling continues;
    ///     a blip is not an answer.</item>
    ///   <item>The final delay is capped to the time remaining, so the call
    ///     never runs past <paramref name="timeout"/>.</item>
    /// </list>
    /// </remarks>
    public async Task<bool> RefreshAfterUpgradeAsync(
        TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken ct = default) {
      var cached = Cached();
      // No stored license: ValidateAsync would be a silent no-op, so polling
      // could only ever time out. Say so immediately and send nothing.
      if (cached == null || string.IsNullOrEmpty(cached.LicenseKey)) return false;
      if (ct.IsCancellationRequested) return false;

      var totalMillis = (long)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds;
      var pollMillis = Math.Max(100L, (long)(pollInterval ?? TimeSpan.FromSeconds(2)).TotalMilliseconds);

      var beforeEntitlements = SnapshotEntitlements();
      var beforeState = State;

      // A local stopwatch rather than _monotonicMillis: the delays below are
      // real wall time, and a test-injected monotonic clock that never advances
      // would otherwise turn the timeout into an infinite loop.
      var clock = System.Diagnostics.Stopwatch.StartNew();
      while (true) {
        // Validate first, sleep second — same order as Swift and Rust. The
        // webhook has often already landed by the time the app regains focus,
        // and a poll that sleeps before its first look wastes a full interval.
        try {
          await ValidateAsync(ct).ConfigureAwait(false);
        } catch {
          // Transient — ValidateAsync already swallows transport failures; this
          // additionally absorbs a LeaseVerificationFailedException so one bad
          // payload does not end the wait early.
        }
        if (ct.IsCancellationRequested) return false;

        if (State != beforeState) return true;
        if (!beforeEntitlements.SetEquals(SnapshotEntitlements())) return true;

        var remaining = totalMillis - clock.ElapsedMilliseconds;
        if (remaining <= 0) return false;

        try {
          await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(pollMillis, remaining)), ct)
            .ConfigureAwait(false);
        } catch (OperationCanceledException) {
          return false;
        }
        if (ct.IsCancellationRequested) return false;
      }
    }

    /// <summary>The entitlement set <see cref="HasEntitlement"/> would consult
    /// right now: the trusted lease's keys, or empty when there is none.</summary>
    private System.Collections.Generic.HashSet<string> SnapshotEntitlements() {
      var set = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
      var lease = GetCachedTrustedLease();
      if (lease?.Entitlements != null)
        foreach (var e in lease.Entitlements) set.Add(e);
      return set;
    }

    /// <summary>
    /// Deactivates this device. Clears the local cache regardless of whether
    /// the server call succeeds, mirroring JS/Rust parity.
    /// </summary>
    public async Task DeactivateAsync(CancellationToken ct = default) {
      var instanceId = Cached()?.InstanceId;

      if (!string.IsNullOrEmpty(instanceId)) {
        try {
          await _transport.DeactivateAsync(
              new DeactivateRequest { LicenseKey = Cached()?.LicenseKey ?? "", InstanceId = instanceId! }, ct)
            .ConfigureAwait(false);
        } catch {
          // Swallow network errors — cache is cleared regardless.
        }
      }

      _store.Clear();
      RefreshCache();
    }

    /// <summary>
    /// Called on app launch: always performs a server <see cref="ValidateAsync"/>
    /// round-trip (unlike <see cref="RefreshIfNeededAsync"/>, this is never
    /// skipped for staleness reasons) so a dashboard revoke or expiry takes
    /// effect on the very next launch instead of lagging behind the in-session
    /// refresh cadence. A transient/thrown validate failure leaves the cached
    /// state untouched (see <see cref="ValidateAsync"/>). Also auto-starts the
    /// trial (persists <c>TrialStartedAt</c>) on the first launch when
    /// <see cref="KeylightConfig.TrialDurationDays"/> is configured and no
    /// trusted active license is present.
    /// </summary>
    public async Task CheckOnLaunchAsync(CancellationToken ct = default) {
      // Prime the cache before any reads below.
      RefreshCache();

      if (_cachedState != null)
        await ValidateAsync(ct).ConfigureAwait(false);
      // Note: ValidateAsync may call RefreshCache() internally (via
      // _store.Save → RefreshCache), so _cachedState reflects the latest
      // state after it returns.

      var trusted = GetCachedTrustedLease();
      bool hasActiveLicense = trusted != null && trusted.Status == "active";

      if (!hasActiveLicense) {
        // An unlicensed install makes no other call that could carry the
        // server-owned settings. The other SDKs get them free on the keyless
        // beacon; this one has no beacon, so /config is the only route that
        // reaches a trial user — without this fetch the dashboard setting would
        // never reach exactly the population it is for. Licensed installs skip
        // it: ValidateAsync above already carried the settings.
        await FetchConfigAsync(ct).ConfigureAwait(false);

        // Auto-start the trial: once, idempotent, and UNCONDITIONALLY — note
        // there is no `duration > 0` guard here, deliberately.
        //
        // Once the duration is server-owned, 0 is indistinguishable from "the
        // config has not arrived yet", so skipping the stamp leaves a
        // later-arriving duration nothing to measure from and the user never
        // gets the trial their tenant enabled. The stamp grants nothing on its
        // own: CheckTrialOrInvalid still reports Invalid while the effective
        // duration is 0.
        //
        // An existing stamp is never overwritten, so enabling a trial months
        // after an install does not hand it a fresh window — otherwise it would
        // be farmable by reinstalling.
        var current = _cachedState ?? new CachedState { FetchedAt = _nowSeconds() };
        if (!current.TrialStartedAt.HasValue) {
          current.TrialStartedAt = _nowSeconds();
          _store.Save(current);
          RefreshCache();
        }
      }
    }

    // ─── server-owned product config ─────────────────────────────────────────

    /// <summary>
    /// Trial length actually in force, in days: server value → local seed → 0.
    /// </summary>
    /// <remarks>
    /// <see cref="KeylightConfig.TrialDurationDays"/> is demoted to a <i>seed</i>,
    /// used only before this install has ever heard from the server. It is
    /// deliberately not removed: a brand-new install genuinely has nothing else,
    /// and dropping it would make first-launch behaviour depend on the network.
    /// </remarks>
    public int EffectiveTrialDurationDays() {
      var cached = Cached()?.ProductTrialDurationDays;
      if (cached.HasValue) return cached.Value;
      return _config.TrialDurationDays ?? 0;
    }

    /// <summary>
    /// Free-tier flag actually in force: server value → <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Same precedence as <see cref="EffectiveTrialDurationDays"/>. There is no
    /// local seed for this one — <see cref="KeylightConfig"/> has never carried
    /// a free-tier setting, so an install that has not yet heard from the
    /// server reports <c>false</c>. The SDK only reports the flag; it does not
    /// gate anything on it (<see cref="KeylightState"/> has no FreeTier member),
    /// so what a free tier unlocks is the host app's decision.
    /// </remarks>
    public bool EffectiveFreeTierEnabled() {
      var cached = Cached()?.ProductFreeTierEnabled;
      if (cached.HasValue) return cached.Value;
      return false;
    }

    /// <summary>
    /// Explicitly refresh the product config from <c>GET /{tenant}/{product}/config</c>.
    /// Never throws.
    /// </summary>
    /// <remarks>
    /// A no-op when the transport does not implement
    /// <see cref="IKeylightConfigTransport"/>. Failures are swallowed: a refresh
    /// that cannot reach the network leaves the last known settings in place
    /// rather than falling back to the seed.
    /// </remarks>
    public async Task FetchConfigAsync(CancellationToken ct = default) {
      if (_transport is not IKeylightConfigTransport configTransport) return;
      try {
        var resp = await configTransport.FetchConfigAsync(ct).ConfigureAwait(false);
        if (resp != null) AbsorbConfigFields(resp.ConfigFields, resp.ConfigSignature);
      } catch {
        // Best-effort: keep the cached settings.
      }
    }

    /// <summary>
    /// Merge server-sent settings into the cache, <b>field by field</b>.
    /// </summary>
    /// <remarks>
    /// A response carrying neither field leaves the cache untouched — an older
    /// worker that knows nothing about these settings must not wipe what this
    /// install already learned. Each field is written only when the server
    /// actually sent it, rather than overwriting the pair.
    /// </remarks>
    internal void AbsorbConfigFields(ProductConfigFields fields, ConfigSignature? signature = null) {
      if (fields == null || fields.IsEmpty) return;

      // The one place signatures are checked, and deliberately the only one. The
      // settings ride on both /config and validate (this SDK has no keyless
      // beacon); verifying at any single route would leave the other as an
      // unauthenticated way to write the same cache. Authentication is a
      // property of the fields, not of the endpoint they arrived on.
      //
      // An unsigned response fails exactly as a badly signed one does —
      // otherwise stripping the signature would be enough to bypass the check.
      // Rejection keeps the cached settings: fall back to the seed, never to
      // what the server claimed.
      if (_config.RequireSignedConfig) {
        if (signature == null) return;
        if (!Verifier.VerifyConfig(
              fields, signature, _config.TenantId, _config.ProductId,
              _config.TrustedKeys, _nowSeconds()))
          return;
      }

      var current = Cached() ?? new CachedState { FetchedAt = _nowSeconds() };
      if (fields.TrialDurationDays.HasValue)
        current.ProductTrialDurationDays = fields.TrialDurationDays;
      if (fields.FreeTierEnabled.HasValue)
        current.ProductFreeTierEnabled = fields.FreeTierEnabled;
      _store.Save(current);
      RefreshCache();
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

    /// <summary>
    /// Returns the current <see cref="CachedState"/>, priming the cache from
    /// the store on first use. Every path that needs the stored state goes
    /// through here, so "load from disk at most once" is stated in exactly one
    /// place instead of being re-derived at each call site.
    /// </summary>
    private CachedState? Cached() {
      if (!_cachePopulated) RefreshCache();
      return _cachedState;
    }

    // ─── private helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the cached lease only if it passes signature verification AND
    /// is within the offline grace window AND is not expired. This is the
    /// "gated" lease used by HasEntitlement.
    /// </summary>
    private Lease? GetCachedTrustedLease() {
      var cached = Cached();
      var now = _nowSeconds();

      if (_config.MaxOfflineDays > 0) {
        if (cached == null) return null;
        if (IsBeyondOfflineCap(cached, now)) return null;
      }

      var raw = cached?.Lease;
      if (raw == null) return null;

      // Reuse cached KidKnown + SignatureValid; recompute Expired fresh.
      var r = _cachedVerifyResult!.Value;
      bool expired = now > raw.ExpiresAt + Verifier.SkewSeconds;
      return (Verifier.IsTrusted(r) && !expired && raw.Status != "expired") ? raw : null;
    }

    /// <summary>
    /// True when the last successful server contact is older than
    /// <see cref="KeylightConfig.MaxOfflineDays"/>. Callers must have already
    /// checked that the cap is enabled (<c>MaxOfflineDays &gt; 0</c>); a value
    /// of zero or less disables the cap entirely (air-gapped consumers).
    /// </summary>
    private bool IsBeyondOfflineCap(CachedState cached, long now)
      => (now - cached.FetchedAt) > (long)_config.MaxOfflineDays * 86400;

    /// <summary>
    /// Resolves <see cref="KeylightState"/> from the raw cached lease (no
    /// offline-grace gating for the status read — mirrors JS state() logic).
    /// </summary>
    private KeylightState ResolveState() {
      var cached = Cached();
      var now = _nowSeconds();

      var rawLease = cached?.Lease;
      if (rawLease == null) return CheckTrialOrInvalid();

      // Reuse cached KidKnown + SignatureValid; recompute Expired fresh.
      var r = _cachedVerifyResult!.Value;
      if (!Verifier.IsTrusted(r)) return CheckTrialOrInvalid();

      // Only "active" can resolve to anything other than Expired: a trusted
      // lease that is expired / fallback (limited — the C# enum has no Limited
      // member) / an unrecognised status all read as Expired.
      if (rawLease.Status != "active") return KeylightState.Expired;

      if (now > rawLease.ExpiresAt + Verifier.SkewSeconds) return KeylightState.Expired;

      // Bound offline use: a signed lease can outlive MaxOfflineDays of no
      // successful server contact. Once that cap is exceeded, State must stop
      // reporting Licensed even though the cached lease itself hasn't expired
      // yet — mirrors the gate GetCachedTrustedLease already applies to
      // HasEntitlement.
      if (_config.MaxOfflineDays > 0 && IsBeyondOfflineCap(cached!, now))
        return KeylightState.Expired;

      return KeylightState.Licensed;
    }

    private KeylightState CheckTrialOrInvalid() {
      var duration = EffectiveTrialDurationDays();
      if (duration > 0) {
        var trialStartedAt = Cached()?.TrialStartedAt;
        if (trialStartedAt.HasValue) {
          var trialEndSeconds = trialStartedAt.Value + (long)duration * 86400L;
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
