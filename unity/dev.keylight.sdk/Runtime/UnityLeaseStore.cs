#if UNITY_2021_3_OR_NEWER
using System.IO;
using UnityEngine;

namespace Keylight.Unity {
  /// <summary>
  /// Unity-specific <see cref="ILeaseStore"/> that persists the license lease
  /// under <c>Application.persistentDataPath/keylight-lease.json</c>.
  ///
  /// <c>Application.persistentDataPath</c> is writable on all Unity platforms
  /// (PC, Mac, iOS, Android, WebGL) without extra permissions.
  /// </summary>
  public sealed class UnityLeaseStore : ILeaseStore {
    private readonly string _filePath;

    /// <param name="filename">
    /// Optional override for the JSON filename (default: <c>keylight-lease.json</c>).
    /// </param>
    public UnityLeaseStore(string filename = "keylight-lease.json") {
      _filePath = Path.Combine(Application.persistentDataPath, filename);
    }

    /// <inheritdoc />
    public CachedState? Load() {
      if (!File.Exists(_filePath)) return null;
      try {
        var raw = File.ReadAllText(_filePath);
        return FileLeaseStore.DeserializeCachedState(raw);
      } catch {
        return null;
      }
    }

    /// <inheritdoc />
    public void Save(CachedState state) {
      var json = FileLeaseStore.SerializeCachedState(state);
      File.WriteAllText(_filePath, json);
    }

    /// <inheritdoc />
    public void Clear() {
      if (File.Exists(_filePath))
        File.Delete(_filePath);
    }
  }
}
#endif // UNITY_2021_3_OR_NEWER
