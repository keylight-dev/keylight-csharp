using System;
using System.Threading.Tasks;
using Keylight;
using Xunit;

namespace Keylight.Tests {
  public class KeylessHeartbeatTests {
    private const long T = 1_700_000_000L;

    private static KeylightClient Client(KeylessTransport t, TimeSpan interval, MemoryLeaseStore? store = null, int? trialDays = null) {
      var b = KeylightConfig.Builder("testco", "testapp", "k").KeylessHeartbeat(interval);
      if (trialDays.HasValue) b = b.TrialDurationDays(trialDays.Value);
      return new KeylightClient(b.Build(), store ?? new MemoryLeaseStore(), t, () => T, null, new FakeDevice("hardware-1"));
    }

    [Fact]
    public void Default_interval_is_six_hours() {
      Assert.Equal(TimeSpan.FromHours(6), KeylightConfig.Builder("t", "p", "k").Build().KeylessHeartbeatInterval);
    }

    [Fact]
    public async Task Launch_beacons_for_an_unlicensed_install_and_carries_the_settings() {
      var t = new KeylessTransport { Reply = _ => new KeylessResponse { Received = true, TrialDurationDays = 21 } };
      using var c = Client(t, TimeSpan.FromHours(1), trialDays: 14);
      await c.CheckOnLaunchAsync();
      Assert.Single(t.Beacons);
      Assert.Equal("trial", t.Beacons[0].State);
      Assert.Equal(21, c.EffectiveTrialDurationDays());
    }

    [Fact]
    public async Task First_tick_is_at_plus_interval_never_immediate() {
      var t = new KeylessTransport();
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { FetchedAt = T, KeylessLastState = "free_tier", LastKeylessPingAt = T - 10, ProductFreeTierEnabled = true });
      using var c = Client(t, TimeSpan.FromHours(1), store);
      c.StartKeylessHeartbeat();
      await Task.Delay(150);
      Assert.Empty(t.Beacons);
    }

    [Fact]
    public async Task Ticks_beacon_when_the_state_calls_for_it() {
      var t = new KeylessTransport();
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { FetchedAt = T, ProductFreeTierEnabled = true });
      using var c = Client(t, TimeSpan.FromMilliseconds(30), store);
      c.StartKeylessHeartbeat();
      await Task.Delay(300);
      Assert.NotEmpty(t.Beacons);
      Assert.Equal("free_tier", t.Beacons[0].State);
      Assert.Single(t.Beacons); // 24h debounce: one beacon, however many ticks
    }

    [Fact]
    public async Task A_licensed_device_never_beacons() {
      var (lease, keys, now) = Vectors.Get("valid-active");
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = lease, InstanceId = "i", LicenseKey = "KEY-1", FetchedAt = now });
      var t = new KeylessTransport();
      using var c = new KeylightClient(
        KeylightConfig.Builder("tenant1", "product1", "k").TrustedKeys(keys).KeylessHeartbeat(TimeSpan.FromMilliseconds(30)).Build(),
        store, t, () => now, null, new FakeDevice("hardware-1"));
      c.StartKeylessHeartbeat();
      await Task.Delay(200);
      Assert.Empty(t.Beacons);
    }

    [Fact]
    public async Task Zero_interval_disables_the_heartbeat() {
      var t = new KeylessTransport();
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { FetchedAt = T, ProductFreeTierEnabled = true });
      using var c = Client(t, TimeSpan.Zero, store);
      c.StartKeylessHeartbeat();
      await Task.Delay(150);
      Assert.Empty(t.Beacons);
    }

    [Fact]
    public async Task Dispose_stops_the_timer() {
      var t = new KeylessTransport();
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { FetchedAt = T, ProductFreeTierEnabled = true });
      var c = Client(t, TimeSpan.FromMilliseconds(30), store);
      c.StartKeylessHeartbeat();
      c.Dispose();
      t.Beacons.Clear();
      await Task.Delay(200);
      Assert.Empty(t.Beacons);
    }
  }
}
