using System.Collections.Generic;
using Keylight;
using Xunit;

namespace Keylight.Tests {
  public class FreeTierStateTests {
    private const long T = 1_700_000_000L;

    private static KeylightClient Client(MemoryLeaseStore store, bool freeTierSeed = false, int? trialDays = null, long now = T) {
      var b = KeylightConfig.Builder("tenant1", "product1", "sdk-key-test");
      if (trialDays.HasValue) b = b.TrialDurationDays(trialDays.Value);
      var cfg = b.Build();
      // Free tier has no compiled-in seed: it arrives from the server. Seed the store the way a beacon reply would.
      var s = store.Load() ?? new CachedState { FetchedAt = now };
      if (freeTierSeed) { s.ProductFreeTierEnabled = true; store.Save(s); }
      return new KeylightClient(cfg, store: store, transport: new FakeTransport(_ => new ActivateResponse { Activated = false }), nowSeconds: () => now);
    }

    [Fact]
    public void Enum_appends_new_members_after_Invalid() {
      Assert.Equal(3, (int)KeylightState.Invalid);
      Assert.Equal(4, (int)KeylightState.FreeTier);
      Assert.Equal(5, (int)KeylightState.Limited);
    }

    [Fact]
    public void Wire_strings_match_the_other_sdks() {
      Assert.Equal("trial", KeylessStateWire.Of(KeylessState.Trial));
      Assert.Equal("free_tier", KeylessStateWire.Of(KeylessState.FreeTier));
      Assert.Equal("expired", KeylessStateWire.Of(KeylessState.Expired));
    }

    [Fact]
    public void Keyless_state_for_each_license_state() {
      Assert.Equal(KeylessState.Trial, KeylessStateWire.For(KeylightState.Trial));
      Assert.Equal(KeylessState.FreeTier, KeylessStateWire.For(KeylightState.FreeTier));
      Assert.Equal(KeylessState.Expired, KeylessStateWire.For(KeylightState.Expired));
      Assert.Null(KeylessStateWire.For(KeylightState.Licensed));
      Assert.Null(KeylessStateWire.For(KeylightState.Limited));
      Assert.Null(KeylessStateWire.For(KeylightState.Invalid));
    }

    [Fact]
    public void Disabled_free_tier_with_nothing_else_is_Invalid() {
      Assert.Equal(KeylightState.Invalid, Client(new MemoryLeaseStore()).State);
    }

    [Fact]
    public void Enabled_free_tier_with_no_trial_is_FreeTier() {
      Assert.Equal(KeylightState.FreeTier, Client(new MemoryLeaseStore(), freeTierSeed: true).State);
    }

    [Fact]
    public void Active_trial_outranks_free_tier() {
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { FetchedAt = T, TrialStartedAt = T - 86400 });
      Assert.Equal(KeylightState.Trial, Client(store, freeTierSeed: true, trialDays: 14).State);
    }

    [Fact]
    public void Elapsed_trial_drops_to_FreeTier_not_Expired() {
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { FetchedAt = T, TrialStartedAt = T - 30 * 86400 });
      Assert.Equal(KeylightState.FreeTier, Client(store, freeTierSeed: true, trialDays: 14).State);
    }

    [Fact]
    public void Elapsed_trial_without_free_tier_is_still_Expired() {
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { FetchedAt = T, TrialStartedAt = T - 30 * 86400 });
      Assert.Equal(KeylightState.Expired, Client(store, trialDays: 14).State);
    }

    [Fact]
    public void A_stored_license_with_no_lease_is_Expired_even_with_free_tier() {
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { FetchedAt = T, LicenseKey = "KEY-1" });
      Assert.Equal(KeylightState.Expired, Client(store, freeTierSeed: true).State);
    }

    [Fact]
    public void Fallback_lease_is_Limited() {
      // Lease.Status is part of the signed payload, so it cannot be mutated
      // without invalidating the signature. Use the conformance vector whose
      // status is already "fallback" instead of a mutated "valid-active" lease.
      var (lease, keys, now) = Vectors.Get("fallback-status");
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = lease, LicenseKey = "KEY-1", InstanceId = "i", FetchedAt = now });
      var client = ClientHelper.MakeClient(ClientHelper.MakeConfig(keys),
        new FakeTransport(_ => new ActivateResponse { Activated = false }), store, now);
      Assert.Equal(KeylightState.Limited, client.State);
    }
  }
}
