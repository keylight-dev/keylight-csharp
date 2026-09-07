using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Keylight;
using Xunit;

namespace Keylight.Tests {
  /// <summary>
  /// Store access is serialised on one lock, and the keyless heartbeat shares it.
  /// </summary>
  /// <remarks>
  /// The heartbeat tick drives <c>ReportKeylessStateAsync</c> from a thread-pool
  /// timer thread while the app may be inside <c>ActivateAsync</c> /
  /// <c>ValidateAsync</c> / <c>DeactivateAsync</c> on its own. Every one of those
  /// is a load-mutate-save against the same store. Unsynchronised that gives
  /// three failure modes: a beacon overwriting a just-persisted lease, a
  /// <c>Load()</c> landing mid-truncate and returning null so the cache is wiped
  /// and the next save persists the wipe, and two concurrent saves colliding on
  /// the file.
  ///
  /// These tests pin the lock rather than the symptom: the first proves mutual
  /// exclusion deterministically (a store that parks inside <c>Save</c> blocks
  /// every other writer), the second drives the real <see cref="FileLeaseStore"/>
  /// and asserts the license survives the race.
  /// </remarks>
  public class StateLockTests {
    private const long T = 1_700_000_000L;

    private static KeylightConfig Config()
      => KeylightConfig.Builder("testco", "testapp", "sdk-key-test").Build();

    /// <summary>Awaits <paramref name="task"/> but fails the test rather than
    /// hanging the run if the lock is ever taken and not released.</summary>
    private static async Task WithTimeout(Task task, int millis, string what) {
      var finished = await Task.WhenAny(task, Task.Delay(millis)).ConfigureAwait(false);
      Assert.True(ReferenceEquals(finished, task), what);
      await task.ConfigureAwait(false);
    }

    /// <summary>A transport that both activates and beacons, so one client can
    /// run an app-initiated write and a beacon against the same store.</summary>
    private sealed class BeaconAndActivateTransport : IKeylightTransport, IKeylightKeylessTransport {
      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => Task.FromResult(new ActivateResponse { Activated = true, InstanceId = "inst-1" });
      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default)
        => Task.FromResult(new ValidateResponse { Valid = true });
      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
        => Task.CompletedTask;
      public Task<KeylessResponse?> ReportKeylessAsync(KeylessRequest req, CancellationToken ct = default)
        => Task.FromResult<KeylessResponse?>(new KeylessResponse { Received = true });
    }

    /// <summary>A store that parks the very first <c>Save</c> until the test
    /// releases it, holding whatever lock its caller took.</summary>
    private sealed class GatedStore : ILeaseStore {
      private CachedState? _state;
      private int _saves;
      public readonly ManualResetEventSlim FirstSaveEntered = new ManualResetEventSlim(false);
      public readonly ManualResetEventSlim ReleaseFirstSave = new ManualResetEventSlim(false);

      public CachedState? Load() => Volatile.Read(ref _state);
      public void Save(CachedState s) {
        if (Interlocked.Increment(ref _saves) == 1) {
          FirstSaveEntered.Set();
          ReleaseFirstSave.Wait(10_000);
        }
        Volatile.Write(ref _state, s);
      }
      public void Clear() => Volatile.Write(ref _state, null);
    }

    [Fact]
    public async Task A_beacon_inside_the_store_blocks_an_app_initiated_write() {
      var store = new GatedStore();
      using var c = new KeylightClient(
        Config(), store, new BeaconAndActivateTransport(), () => T, null, new FakeDevice("hardware-1"));

      // Park the beacon inside its first store write, holding the state lock.
      var beacon = Task.Run(() => c.ReportKeylessStateAsync(KeylessState.Trial));
      Assert.True(store.FirstSaveEntered.Wait(10_000), "the beacon never reached the store");

      // With the lock in place the activate cannot get at the store at all.
      var activate = Task.Run(() => c.ActivateAsync("KEY-1"));
      await Task.Delay(400);
      Assert.False(activate.IsCompleted,
        "ActivateAsync ran its load-mutate-save while the beacon held the store");

      store.ReleaseFirstSave.Set();
      await WithTimeout(beacon, 10_000, "the beacon did not finish");
      await WithTimeout(activate, 10_000, "the activate did not finish");

      // Neither writer clobbered the other: the license landed and the beacon's
      // anonymous identity survived.
      var final = store.Load();
      Assert.NotNull(final);
      Assert.Equal("KEY-1", final!.LicenseKey);
      Assert.Equal("inst-1", final.InstanceId);
      Assert.False(string.IsNullOrEmpty(final.FreeTierInstanceId));
    }

    [Fact]
    public async Task A_beacon_racing_activate_on_a_file_store_never_loses_the_license() {
      var dir = Path.Combine(Path.GetTempPath(), "keylight-statelock-" + Guid.NewGuid().ToString("N"));
      try {
        var store = new FileLeaseStore(dir);
        using var c = new KeylightClient(
          Config(), store, new BeaconAndActivateTransport(), () => T, null, new FakeDevice("hardware-1"));

        for (int i = 0; i < 40; i++) {
          // A different state every iteration, so the 24h debounce never
          // short-circuits the beacon and both threads really do write.
          var ks = (KeylessState)(i % 3);
          var beacon = Task.Run(() => c.ReportKeylessStateAsync(ks));
          var activate = Task.Run(() => c.ActivateAsync("KEY-1"));
          await WithTimeout(Task.WhenAll(beacon, activate), 20_000, "a writer hung");

          var s = store.Load();
          Assert.NotNull(s);
          Assert.Equal("KEY-1", s!.LicenseKey);
          Assert.Equal("inst-1", s.InstanceId);
        }
      } finally {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
      }
    }
  }
}
