using System;
using System.IO;
using Keylight.Json;

namespace Keylight {
  /// <summary>Cached lease state persisted between process runs.</summary>
  public sealed class CachedState {
    public Lease?  Lease          { get; set; }
    public string? InstanceId     { get; set; }
    /// <summary>
    /// The activated license key. Persisted because /validate and /deactivate
    /// both require it on the wire — without it every check-in 400s and the
    /// client degrades to activate-only. Null on installs that activated before
    /// this field existed, and on trial-only devices that never activated.
    /// </summary>
    public string? LicenseKey     { get; set; }
    public long    FetchedAt      { get; set; }
    /// <summary>
    /// Unix-second timestamp of when the trial was started on this device.
    /// Null until the first <see cref="KeylightClient.CheckOnLaunchAsync"/> call
    /// on a fresh install with <c>TrialDurationDays</c> configured.
    /// Once set it is never reset (idempotent trial start).
    /// </summary>
    public long?   TrialStartedAt { get; set; }
    /// <summary>
    /// Trial length last heard from the server, in days. Cached so an offline
    /// launch uses the tenant's real setting rather than the compiled-in seed.
    /// <para>
    /// <b>Null means "never heard from the server", which is not the same as 0.</b>
    /// A tenant who turns trials off in the dashboard sends a real 0; collapsing
    /// that into "absent" would fall back to the seed and silently re-enable the
    /// trial they just disabled. Nullable for exactly that reason — do not
    /// replace it with an int and a sentinel.
    /// </para>
    /// </summary>
    public int?    ProductTrialDurationDays { get; set; }
    /// <summary>
    /// Free-tier flag last heard from the server — over <c>/config</c>, validate,
    /// or the keyless beacon. Read by
    /// <see cref="KeylightClient.EffectiveFreeTierEnabled"/>: when it is on and
    /// no license or running trial applies, <see cref="KeylightClient.State"/>
    /// resolves to <see cref="KeylightState.FreeTier"/> and the beacon reports
    /// this device as <c>free_tier</c>.
    /// </summary>
    public bool?   ProductFreeTierEnabled { get; set; }
    /// <summary>Anonymous per-install id for the keyless beacon and for
    /// free-tier → paid attribution on activate. Minted once, never rotated.</summary>
    public string? FreeTierInstanceId { get; set; }
    /// <summary>Wire string of the last state the beacon successfully reported
    /// (<c>trial</c> / <c>free_tier</c> / <c>expired</c>). Debounce input.</summary>
    public string? KeylessLastState { get; set; }
    /// <summary>Unix seconds of the last HTTP-2xx beacon. Debounce input.</summary>
    public long?   LastKeylessPingAt { get; set; }
    /// <summary>Last successfully read hardware id, so <c>machine_hash</c> stays
    /// stable across a transient probe failure. Never a random value.</summary>
    public string? CachedHardwareId { get; set; }
  }

  /// <summary>Pluggable storage backend for the cached lease state.</summary>
  public interface ILeaseStore {
    /// <summary>Returns the cached state, or null if absent or unparseable.</summary>
    CachedState? Load();
    /// <summary>Persists the cached state.</summary>
    void Save(CachedState state);
    /// <summary>Removes the cached state.</summary>
    void Clear();
  }

  /// <summary>
  /// JSON-file-backed lease store.  The file is written to <paramref name="directory"/>
  /// (created on first save).  When <paramref name="directory"/> is null the store
  /// defaults to the per-OS application-data folder joined with "Keylight".
  /// </summary>
  public sealed class FileLeaseStore : ILeaseStore {
    private readonly string _filePath;

    public FileLeaseStore(string? directory = null) {
      var dir = directory
        ?? Path.Combine(
             Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
             "Keylight");
      _filePath = Path.Combine(dir, "keylight-lease.json");
    }

    /// <inheritdoc />
    public CachedState? Load() {
      if (!File.Exists(_filePath)) return null;
      try {
        var raw = File.ReadAllText(_filePath);
        return DeserializeCachedState(raw);
      } catch {
        return null;
      }
    }

    /// <inheritdoc />
    public void Save(CachedState state) {
      var dir = Path.GetDirectoryName(_filePath);
      if (dir != null && !Directory.Exists(dir))
        Directory.CreateDirectory(dir);
      var json = SerializeCachedState(state);
      File.WriteAllText(_filePath, json);
    }

    /// <inheritdoc />
    public void Clear() {
      if (File.Exists(_filePath))
        File.Delete(_filePath);
    }

    // -------------------------------------------------------------------
    // Serialization helpers (internal, zero external dependency)
    // -------------------------------------------------------------------

    internal static string SerializeCachedState(CachedState state) {
      // Lease is serialized as a sub-object using WireHelpers
      // We build the JSON string manually to include the lease sub-object
      var sb = new System.Text.StringBuilder();
      sb.Append('{');

      bool first = true;

      // lease (nullable)
      if (state.Lease != null) {
        if (!first) sb.Append(',');
        first = false;
        sb.Append("\"lease\":");
        var leaseVal = WireHelpers.LeaseToJsonValue(state.Lease);
        sb.Append(JsonCodec.StringifyValue(leaseVal));
      }

      // instanceId (nullable string)
      if (state.InstanceId != null) {
        if (!first) sb.Append(',');
        first = false;
        sb.Append("\"instanceId\":");
        sb.Append('"');
        JsonCodec.WriteEscapedString(sb, state.InstanceId);
        sb.Append('"');
      }

      // licenseKey (nullable string)
      if (state.LicenseKey != null) {
        if (!first) sb.Append(',');
        first = false;
        sb.Append("\"licenseKey\":");
        sb.Append('"');
        JsonCodec.WriteEscapedString(sb, state.LicenseKey);
        sb.Append('"');
      }

      // fetchedAt (long)
      if (!first) sb.Append(',');
      sb.Append("\"fetchedAt\":");
      sb.Append(state.FetchedAt.ToString(System.Globalization.CultureInfo.InvariantCulture));

      // trialStartedAt (nullable long)
      if (state.TrialStartedAt.HasValue) {
        sb.Append(",\"trialStartedAt\":");
        sb.Append(state.TrialStartedAt.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
      }

      // Server-owned product settings. Written only when present: a missing key
      // round-trips back to null, which is what preserves the absent-vs-zero
      // distinction the whole feature turns on.
      if (state.ProductTrialDurationDays.HasValue) {
        sb.Append(",\"productTrialDurationDays\":");
        sb.Append(state.ProductTrialDurationDays.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
      }
      if (state.ProductFreeTierEnabled.HasValue) {
        sb.Append(",\"productFreeTierEnabled\":");
        sb.Append(state.ProductFreeTierEnabled.Value ? "true" : "false");
      }

      AppendString(sb, "freeTierInstanceId", state.FreeTierInstanceId);
      AppendString(sb, "keylessLastState", state.KeylessLastState);
      if (state.LastKeylessPingAt.HasValue) {
        sb.Append(",\"lastKeylessPingAt\":");
        sb.Append(state.LastKeylessPingAt.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
      }
      AppendString(sb, "cachedHardwareId", state.CachedHardwareId);

      sb.Append('}');
      return sb.ToString();
    }

    private static void AppendString(System.Text.StringBuilder sb, string key, string? value) {
      if (value == null) return;
      sb.Append(",\"").Append(key).Append("\":\"");
      JsonCodec.WriteEscapedString(sb, value);
      sb.Append('"');
    }

    internal static CachedState? DeserializeCachedState(string json) {
      var root = JsonCodec.Parse(json);
      if (root == null) return null;

      var state = new CachedState();
      state.InstanceId = root.Get("instanceId")?.AsString();
      state.LicenseKey = root.Get("licenseKey")?.AsString();
      state.FetchedAt  = root.Get("fetchedAt")?.AsLong() ?? 0;

      state.TrialStartedAt = root.Get("trialStartedAt")?.AsLong();
      var cachedDays = root.Get("productTrialDurationDays")?.AsLong();
      state.ProductTrialDurationDays = cachedDays.HasValue ? (int)cachedDays.Value : (int?)null;
      state.ProductFreeTierEnabled = root.Get("productFreeTierEnabled")?.AsBool();
      state.Lease = WireHelpers.ParseLease(root.Get("lease"));

      state.FreeTierInstanceId = root.Get("freeTierInstanceId")?.AsString();
      state.KeylessLastState   = root.Get("keylessLastState")?.AsString();
      state.LastKeylessPingAt  = root.Get("lastKeylessPingAt")?.AsLong();
      state.CachedHardwareId   = root.Get("cachedHardwareId")?.AsString();

      return state;
    }
  }
}
