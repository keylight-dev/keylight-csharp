using System;
using System.Threading;
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

    /// <summary>A transport that can do both: the beacon AND the /config fetch.
    /// The shape the real <c>HttpClientTransport</c> and
    /// <c>UnityWebRequestTransport</c> have.</summary>
    private sealed class BeaconAndConfigTransport : KeylessTransport, IKeylightConfigTransport {
      public int ConfigFetches;
      public ConfigResponse? Config = new ConfigResponse { TrialDurationDays = 21, FreeTierEnabled = true };
      public Task<ConfigResponse?> FetchConfigAsync(CancellationToken ct = default) {
        ConfigFetches++;
        return Task.FromResult(Config);
      }
    }

    [Fact]
    public async Task A_fresh_install_with_no_trial_seed_still_learns_the_settings_at_launch() {
      // The resting state of a fresh install with no compiled-in trial seed is
      // Invalid: no lease, no trial duration, and free tier off until the server
      // says otherwise. KeylessStateWire.For(Invalid) is null — Invalid is a
      // denial, not a device worth counting — so there is no beacon to send.
      // If launch stopped there, the dashboard's trial length and free-tier flag
      // would never reach the install: it would sit Invalid forever and never
      // beacon. It must fall back to /config, exactly as 0.4.1 did.
      var t = new BeaconAndConfigTransport();
      using var c = new KeylightClient(
        KeylightConfig.Builder("testco", "testapp", "k").Build(),
        new MemoryLeaseStore(), t, () => T, null, new FakeDevice("hardware-1"));

      await c.CheckOnLaunchAsync();

      Assert.Empty(t.Beacons);              // nothing to report for Invalid
      Assert.Equal(1, t.ConfigFetches);     // but the settings still arrived
      Assert.Equal(21, c.EffectiveTrialDurationDays());
      Assert.True(c.EffectiveFreeTierEnabled());
    }

    [Fact]
    public async Task A_beaconable_state_uses_the_beacon_and_not_the_config_fetch() {
      // The other half of the same branch: when there IS a state to report the
      // beacon carries the settings, and /config is not called as well.
      var t = new BeaconAndConfigTransport();
      using var c = new KeylightClient(
        KeylightConfig.Builder("testco", "testapp", "k").TrialDurationDays(14).Build(),
        new MemoryLeaseStore(), t, () => T, null, new FakeDevice("hardware-1"));

      await c.CheckOnLaunchAsync();

      Assert.Single(t.Beacons);
      Assert.Equal("trial", t.Beacons[0].State);
      Assert.Equal(0, t.ConfigFetches);
    }

    [Fact]
    public async Task First_tick_is_at_plus_interval_never_immediate() {
      var t = new KeylessTransport();
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { FetchedAt = T, ProductFreeTierEnabled = true });
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

    [Fact]
    public async Task Dispose_before_start_leaves_the_heartbeat_off() {
      // Regression for the Start/Dispose race: _disposed must be checked and
      // set under the same lock as the Timer construction/teardown, or a
      // Dispose that lands between the guard check and the lock acquisition
      // in StartKeylessHeartbeat would leak a rooted, never-disposed Timer.
      var t = new KeylessTransport();
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { FetchedAt = T, ProductFreeTierEnabled = true });
      var c = Client(t, TimeSpan.FromMilliseconds(30), store);
      c.Dispose();
      c.StartKeylessHeartbeat();
      await Task.Delay(200);
      Assert.Empty(t.Beacons);
    }
  }
}
