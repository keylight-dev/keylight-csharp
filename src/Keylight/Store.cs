using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keylight {
  /// <summary>Cached lease state persisted between process runs.</summary>
  public sealed class CachedState {
    [JsonPropertyName("lease")]          public Lease?  Lease          { get; set; }
    [JsonPropertyName("instanceId")]     public string? InstanceId     { get; set; }
    [JsonPropertyName("fetchedAt")]      public long    FetchedAt      { get; set; }
    /// <summary>
    /// Unix-second timestamp of when the trial was started on this device.
    /// Null until the first <see cref="KeylightClient.CheckOnLaunchAsync"/> call
    /// on a fresh install with <c>TrialDurationDays</c> configured.
    /// Once set it is never reset (idempotent trial start).
    /// </summary>
    [JsonPropertyName("trialStartedAt")] public long?   TrialStartedAt { get; set; }
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

    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions {
      PropertyNameCaseInsensitive = true
    };

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
        return JsonSerializer.Deserialize<CachedState>(raw, _jsonOptions);
      } catch {
        // Missing or corrupt file — treat as empty.
        return null;
      }
    }

    /// <inheritdoc />
    public void Save(CachedState state) {
      var dir = Path.GetDirectoryName(_filePath);
      if (dir != null && !Directory.Exists(dir))
        Directory.CreateDirectory(dir);
      var json = JsonSerializer.Serialize(state, _jsonOptions);
      File.WriteAllText(_filePath, json);
    }

    /// <inheritdoc />
    public void Clear() {
      if (File.Exists(_filePath))
        File.Delete(_filePath);
    }
  }
}
