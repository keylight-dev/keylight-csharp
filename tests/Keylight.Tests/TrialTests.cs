using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Keylight;
using Xunit;

namespace Keylight.Tests {

  /// <summary>
  /// Tests for the Trial state: auto-start on first launch, persistence,
  /// idempotency, time-based expiry, and license-wins-over-trial precedence.
  /// </summary>
  public class TrialTests {

    // ─── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Transport that always returns "no license" — activate throws, validate
    /// returns valid=false with no lease.
    /// </summary>
    private class NoLicenseTransport : IKeylightTransport {
      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, System.Threading.CancellationToken ct)
        => Task.FromResult(new ActivateResponse { Activated = false });
      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, System.Threading.CancellationToken ct)
        => Task.FromResult(new ValidateResponse { Valid = false, Error = "no license" });
      public Task DeactivateAsync(DeactivateRequest req, System.Threading.CancellationToken ct)
        => Task.CompletedTask;
    }

    private static KeylightClient MakeTrialClient(
      MemoryLeaseStore store,
      long nowSeconds,
      int trialDays = 14,
      Dictionary<string, string>? trustedKeys = null) {
      var config = KeylightConfig
        .Builder("tenant1", "product1", "sdk-key-test")
        .TrustedKeys(trustedKeys ?? new Dictionary<string, string>())
        .TrialDurationDays(trialDays)
        .Build();
      return new KeylightClient(
        config,
        store: store,
        transport: new NoLicenseTransport(),
        nowSeconds: () => nowSeconds);
    }

    // ─── tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckOnLaunchAsync_fresh_install_sets_TrialStartedAt_and_State_Trial() {
      const long T = 1_700_000_000L;
      var store = new MemoryLeaseStore();
      var client = MakeTrialClient(store, T);

      await client.CheckOnLaunchAsync();

      // Store must have TrialStartedAt == T
      var saved = store.Load();
      Assert.NotNull(saved);
      Assert.Equal(T, saved!.TrialStartedAt);

      // State must be Trial
      Assert.Equal(KeylightState.Trial, client.State);
    }

    [Fact]
    public async Task State_is_Trial_within_window_and_Expired_after() {
      const long T = 1_700_000_000L;

      // --- 13 days later: still Trial ---
      {
        var store = new MemoryLeaseStore();
        long atT13 = T + 13L * 86400L;
        // Pre-seed TrialStartedAt so State can read it synchronously
        store.Save(new CachedState { FetchedAt = T, TrialStartedAt = T });
        var client = MakeTrialClient(store, atT13);
        Assert.Equal(KeylightState.Trial, client.State);
      }

      // --- 15 days later: Expired ---
      {
        var store = new MemoryLeaseStore();
        long atT15 = T + 15L * 86400L;
        store.Save(new CachedState { FetchedAt = T, TrialStartedAt = T });
        var client = MakeTrialClient(store, atT15);
        Assert.Equal(KeylightState.Expired, client.State);
      }
    }

    [Fact]
    public async Task CheckOnLaunchAsync_is_idempotent_does_not_reset_TrialStartedAt() {
      const long T = 1_700_000_000L;
      var store = new MemoryLeaseStore();
      var client = MakeTrialClient(store, T);

      // First call — seeds TrialStartedAt
      await client.CheckOnLaunchAsync();
      var firstSaved = store.Load();
      Assert.Equal(T, firstSaved!.TrialStartedAt);

      // Second call with a later clock — must NOT overwrite TrialStartedAt
      long laterNow = T + 60L;
      var client2 = MakeTrialClient(store, laterNow);
      await client2.CheckOnLaunchAsync();

      var secondSaved = store.Load();
      Assert.Equal(T, secondSaved!.TrialStartedAt); // still the original T
    }

    [Fact]
    public async Task Licensed_state_wins_over_trial_even_when_TrialDurationDays_set() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");

      var config = KeylightConfig
        .Builder("tenant1", "product1", "sdk-key-test")
        .TrustedKeys(trustedKeys)
        .TrialDurationDays(14)
        .Build();

      var store = new MemoryLeaseStore();
      // Pre-populate store with the valid active lease (as if activation already happened)
      store.Save(new CachedState {
        Lease = lease,
        LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001",
        FetchedAt = now,
        // Also seed a trial start to ensure Licensed still wins
        TrialStartedAt = now - 86400L
      });

      var client = new KeylightClient(
        config,
        store: store,
        transport: new NoLicenseTransport(),
        nowSeconds: () => now);

      Assert.Equal(KeylightState.Licensed, client.State);
    }

    [Fact]
    public async Task No_TrialDurationDays_and_no_license_gives_Invalid() {
      const long T = 1_700_000_000L;
      var store = new MemoryLeaseStore();

      var config = KeylightConfig
        .Builder("tenant1", "product1", "sdk-key-test")
        .Build(); // no TrialDurationDays

      var client = new KeylightClient(
        config,
        store: store,
        transport: new NoLicenseTransport(),
        nowSeconds: () => T);

      await client.CheckOnLaunchAsync();

      Assert.Equal(KeylightState.Invalid, client.State);

      // Store must NOT have a TrialStartedAt set
      var saved = store.Load();
      // store.Load() may be null (no lease, no state was written)
      Assert.True(saved == null || !saved.TrialStartedAt.HasValue);
    }

    [Fact]
    public async Task HasEntitlement_is_false_during_trial() {
      const long T = 1_700_000_000L;
      var store = new MemoryLeaseStore();
      var client = MakeTrialClient(store, T);

      await client.CheckOnLaunchAsync();

      Assert.Equal(KeylightState.Trial, client.State);
      // Trial does NOT grant signed entitlements
      Assert.False(client.HasEntitlement("pro"));
      Assert.False(client.HasEntitlement("beta"));
    }
  }
}
