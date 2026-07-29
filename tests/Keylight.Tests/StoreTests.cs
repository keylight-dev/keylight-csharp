using System;
using System.IO;
using Keylight;
using Xunit;

namespace Keylight.Tests {
  public class StoreTests : IDisposable {
    private readonly string _tempDir;
    private readonly FileLeaseStore _store;

    public StoreTests() {
      _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
      _store = new FileLeaseStore(_tempDir);
    }

    public void Dispose() {
      if (Directory.Exists(_tempDir))
        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Load_OnEmptyDir_ReturnsNull() {
      var result = _store.Load();
      Assert.Null(result);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsValues() {
      var lease = new Lease {
        Kid = "kid-abc123",
        LicenseKeyHash = "hash-xyz",
        InstanceId = "inst-001",
        IssuedAt = 1700000000L,
        ExpiresAt = 1800000000L,
        Status = "active",
        Entitlements = new[] { "pro", "beta" },
        Signature = "sig-aabbcc"
      };
      var state = new CachedState {
        Lease = lease,
        LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001",
        FetchedAt = 1700000001L
      };

      _store.Save(state);
      var loaded = _store.Load();

      Assert.NotNull(loaded);
      Assert.NotNull(loaded!.Lease);
      Assert.Equal(lease.Kid, loaded.Lease!.Kid);
      Assert.Equal("inst-001", loaded.InstanceId);
      Assert.Equal(1700000001L, loaded.FetchedAt);
    }

    [Fact]
    public void Clear_ThenLoad_ReturnsNull() {
      var state = new CachedState {
        Lease = new Lease { Kid = "kid-clear-test" },
        InstanceId = "inst-clear",
        FetchedAt = 999L
      };
      _store.Save(state);
      _store.Clear();
      var result = _store.Load();
      Assert.Null(result);
    }
  }
}
