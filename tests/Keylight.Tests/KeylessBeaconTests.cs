using System;
using System.Threading.Tasks;
using Keylight;
using Xunit;

namespace Keylight.Tests {
  public class KeylessBeaconTests {
    private const long T = 1_700_000_000L;

    private static KeylightClient Client(MemoryLeaseStore store, KeylessTransport t, IDeviceIdentity? device = null, Func<long>? now = null)
      => new KeylightClient(
        KeylightConfig.Builder("testco", "testapp", "sdk-key-test").Build(),
        store, t, now ?? (() => T), null, device ?? new FakeDevice("hardware-1"));

    [Fact]
    public void Instance_id_is_minted_once_and_stable() {
      var store = new MemoryLeaseStore();
      var c = Client(store, new KeylessTransport());
      var a = c.FreeTierInstanceId();
      Assert.Equal(36, a.Length);
      Assert.Equal(a, c.FreeTierInstanceId());
      Assert.Equal(a, store.Load()!.FreeTierInstanceId);
      Assert.Equal(a, Client(store, new KeylessTransport()).FreeTierInstanceId());
    }

    [Fact]
    public void Machine_hash_uses_the_canonical_formula_and_is_omitted_without_hardware_id() {
      var store = new MemoryLeaseStore();
      Assert.Equal("8e8871112f28cabda180ada131d0b4f4f07c72fb47c5d884edbe32812885b22a",
        Client(store, new KeylessTransport()).MachineHash());
      Assert.Null(Client(new MemoryLeaseStore(), new KeylessTransport(), new FakeDevice((string?)null)).MachineHash());
      Assert.Null(Client(new MemoryLeaseStore(), new KeylessTransport(), new FakeDevice("")).MachineHash());
    }

    [Fact]
    public void Hardware_id_is_cached_and_survives_a_flaky_read() {
      var store = new MemoryLeaseStore();
      var c = Client(store, new KeylessTransport(), new FakeDevice("hardware-1", null));
      var first = c.MachineHash();
      Assert.Equal("hardware-1", store.Load()!.CachedHardwareId);
      Assert.Equal(first, c.MachineHash()); // second read returns null → cached id used
    }

    [Fact]
    public async Task Beacon_posts_instance_id_state_and_machine_hash() {
      var t = new KeylessTransport();
      var c = Client(new MemoryLeaseStore(), t);
      await c.ReportKeylessStateAsync(KeylessState.Trial);
      var b = Assert.Single(t.Beacons);
      Assert.Equal(c.FreeTierInstanceId(), b.InstanceId);
      Assert.Equal("trial", b.State);
      Assert.Equal(c.MachineHash(), b.MachineHash);
      Assert.Equal(SdkInfo.Version, b.SdkVersion);
    }

    [Fact]
    public async Task Same_state_inside_24h_sends_nothing() {
      var t = new KeylessTransport();
      var c = Client(new MemoryLeaseStore(), t);
      await c.ReportKeylessStateAsync(KeylessState.Trial);
      await c.ReportKeylessStateAsync(KeylessState.Trial);
      Assert.Single(t.Beacons);
    }

    [Fact]
    public async Task Changed_state_defeats_the_debounce() {
      var t = new KeylessTransport();
      var c = Client(new MemoryLeaseStore(), t);
      await c.ReportKeylessStateAsync(KeylessState.Trial);
      await c.ReportKeylessStateAsync(KeylessState.Expired);
      Assert.Equal(2, t.Beacons.Count);
    }

    [Fact]
    public async Task Same_state_sends_again_after_24h() {
      long now = T;
      var t = new KeylessTransport();
      var c = Client(new MemoryLeaseStore(), t, now: () => now);
      await c.ReportKeylessStateAsync(KeylessState.Trial);
      now = T + 86401;
      await c.ReportKeylessStateAsync(KeylessState.Trial);
      Assert.Equal(2, t.Beacons.Count);
    }

    [Fact]
    public async Task Non_2xx_does_not_arm_the_debounce() {
      var t = new KeylessTransport { Throw = new ActivationException(500, "boom") };
      var store = new MemoryLeaseStore();
      var c = Client(store, t);
      await c.ReportKeylessStateAsync(KeylessState.Trial); // swallowed
      Assert.Null(store.Load()?.LastKeylessPingAt);
      t.Throw = null;
      await c.ReportKeylessStateAsync(KeylessState.Trial);
      Assert.Single(t.Beacons);
    }

    [Fact]
    public async Task Debounce_survives_a_relaunch() {
      var store = new MemoryLeaseStore();
      var t = new KeylessTransport();
      await Client(store, t).ReportKeylessStateAsync(KeylessState.Trial);
      await Client(store, t).ReportKeylessStateAsync(KeylessState.Trial);
      Assert.Single(t.Beacons);
    }

    [Fact]
    public async Task Settings_in_the_reply_are_absorbed_through_the_gate() {
      var t = new KeylessTransport { Reply = _ => new KeylessResponse { Received = true, TrialDurationDays = 30, FreeTierEnabled = true } };
      var store = new MemoryLeaseStore();
      var c = Client(store, t);
      await c.ReportKeylessStateAsync(KeylessState.FreeTier);
      Assert.Equal(30, c.EffectiveTrialDurationDays());
      Assert.True(c.EffectiveFreeTierEnabled());
      Assert.Equal(KeylightState.FreeTier, c.State);
    }

    [Fact]
    public async Task With_RequireSignedConfig_an_unsigned_beacon_reply_is_not_cached() {
      var t = new KeylessTransport { Reply = _ => new KeylessResponse { Received = true, TrialDurationDays = 3650 } };
      var cfg = KeylightConfig.Builder("testco", "testapp", "sdk-key-test").RequireSignedConfig().Build();
      var store = new MemoryLeaseStore();
      var c = new KeylightClient(cfg, store, t, () => T, null, new FakeDevice("hardware-1"));
      await c.ReportKeylessStateAsync(KeylessState.Trial);
      Assert.Null(store.Load()?.ProductTrialDurationDays);
      Assert.NotNull(store.Load()?.LastKeylessPingAt); // the beacon itself succeeded
    }

    [Fact]
    public async Task Transport_without_the_capability_is_a_silent_no_op() {
      var c = new KeylightClient(KeylightConfig.Builder("testco", "testapp", "k").Build(),
        new MemoryLeaseStore(), new FakeTransport(_ => new ActivateResponse { Activated = false }), () => T);
      await c.ReportKeylessStateAsync(KeylessState.Trial); // must not throw
    }

    [Fact]
    public async Task A_failing_store_write_never_throws() {
      var t = new KeylessTransport();
      var c = new KeylightClient(
        KeylightConfig.Builder("testco", "testapp", "sdk-key-test").Build(),
        new ThrowingLeaseStore(), t, () => T, null, new FakeDevice("hardware-1"));
      await c.ReportKeylessStateAsync(KeylessState.Trial); // must not throw, even though every Save() does
    }
  }
}
