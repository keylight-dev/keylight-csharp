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

      sb.Append('}');
      return sb.ToString();
    }

    internal static CachedState? DeserializeCachedState(string json) {
      var root = JsonCodec.Parse(json);
      if (root == null) return null;

      var state = new CachedState();
      state.InstanceId = root.Get("instanceId")?.AsString();
      state.LicenseKey = root.Get("licenseKey")?.AsString();
      state.FetchedAt  = root.Get("fetchedAt")?.AsLong() ?? 0;

      state.TrialStartedAt = root.Get("trialStartedAt")?.AsLong();
      state.Lease = WireHelpers.ParseLease(root.Get("lease"));

      return state;
    }
  }
}
