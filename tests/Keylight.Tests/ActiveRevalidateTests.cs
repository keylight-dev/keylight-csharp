using System;
using System.Threading;
using System.Threading.Tasks;
using Keylight;
using Xunit;

namespace Keylight.Tests {

  /// <summary>
  /// Parity with the Swift SDK's <c>activeRevalidate()</c>: a prompt,
  /// consumer-triggered revalidation for foreground / popover-open moments.
  ///
  ///   1. No-op when nothing is stored (nothing to revalidate).
  ///   2. Debounced at 60 s, held in memory only (never persisted).
  ///   3. Forces the server round-trip — bypasses RefreshIfNeededAsync's
  ///      staleness gates so a dashboard revoke lands within minutes instead
  ///      of at the next launch.
  ///   4. A definitive rejection (valid:false, incl. the worker's HTTP 422
  ///      shape) downgrades immediately.
  ///   5. A transient/thrown failure never downgrades a live session.
  /// </summary>
  public class ActiveRevalidateTests {

    // ─── transports ────────────────────────────────────────────────────────

    // Counts validate calls and always confirms the same lease.
    private class CountingConfirmTransport : IKeylightTransport {
      public int ValidateCalls;
      private readonly Lease _lease;
      public CountingConfirmTransport(Lease lease) { _lease = lease; }
      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => throw new NotSupportedException();
      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default) {
        ValidateCalls++;
        return Task.FromResult(new ValidateResponse { Valid = true, Lease = _lease });
      }
      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
        => Task.CompletedTask;
    }

    // Parsed definitive rejection: Valid == false, no lease.
    private class RevokedTransport : IKeylightTransport {
      public int ValidateCalls;
      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => throw new NotSupportedException();
      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default) {
        ValidateCalls++;
        return Task.FromResult(new ValidateResponse { Valid = false, Lease = null, Error = "revoked" });
      }
      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
        => Task.CompletedTask;
    }

    // The exact shape HttpClientTransport surfaces for a real dashboard revoke:
    // HTTP 422 with {"valid":false,"reason":"revoked","error":"..."}.
    private class Real422RevokedTransport : IKeylightTransport {
      public int ValidateCalls;
      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => throw new NotSupportedException();
      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default) {
        ValidateCalls++;
        const string body = "{\"valid\":false,\"reason\":\"revoked\",\"error\":\"License revoked\"}";
        throw new ActivationException(422, $"Keylight API returned HTTP 422: {body}", body);
      }
      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
        => Task.CompletedTask;
    }

    // Genuine transport failure (timeout / DNS / connection refused).
    private class ThrowingValidateTransport : IKeylightTransport {
      public int ValidateCalls;
      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => throw new NotSupportedException();
      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default) {
        ValidateCalls++;
        throw new Exception("network unreachable");
      }
      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
        => Task.CompletedTask;
    }

    // Returns a lease that fails signature verification — ValidateAsync throws
    // LeaseVerificationFailedException out of its verify-or-reject guard.
    private class TamperedLeaseTransport : IKeylightTransport {
      public int ValidateCalls;
      private readonly Lease _lease;
      public TamperedLeaseTransport(Lease lease) { _lease = lease; }
      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => throw new NotSupportedException();
      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default) {
        ValidateCalls++;
        return Task.FromResult(new ValidateResponse { Valid = true, Lease = _lease });
      }
      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
        => Task.CompletedTask;
    }

    // ─── (1) no stored license is a no-op ──────────────────────────────────

    [Fact]
    public async Task ActiveRevalidateAsync_with_empty_store_makes_no_network_call() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore(); // nothing stored
      var transport = new CountingConfirmTransport(lease);
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.ActiveRevalidateAsync();

      Assert.Equal(0, transport.ValidateCalls);
      Assert.Equal(KeylightState.Invalid, client.State);
    }

    [Fact]
    public async Task ActiveRevalidateAsync_with_trial_only_state_makes_no_network_call() {
      // A trial-only device has never activated: no instance id, no lease.
      // There is nothing for the server to revalidate.
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = KeylightConfig
        .Builder("tenant1", "product1", "sdk-key-test")
        .TrustedKeys(trustedKeys)
        .TrialDurationDays(14)
        .Build();
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = null, InstanceId = null, FetchedAt = now, TrialStartedAt = now - 60 });

      var transport = new CountingConfirmTransport(lease);
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.ActiveRevalidateAsync();

      Assert.Equal(0, transport.ValidateCalls);
      Assert.Equal(KeylightState.Trial, client.State);
    }

    // ─── (2) 60 s in-memory debounce ───────────────────────────────────────

    [Fact]
    public async Task ActiveRevalidateAsync_debounce_suppresses_second_call() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = lease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = now });

      var transport = new CountingConfirmTransport(lease);
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.ActiveRevalidateAsync();
      await client.ActiveRevalidateAsync();

      Assert.Equal(1, transport.ValidateCalls);
    }

    [Fact]
    public async Task ActiveRevalidateAsync_calls_again_after_debounce_window_elapses() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = lease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = now });

      // The debounce runs off the MONOTONIC clock, not the wall clock, so drive
      // that one — advancing `nowSeconds` alone no longer moves the window.
      long monotonic = 0;
      var transport = new CountingConfirmTransport(lease);
      var client = new KeylightClient(
        config, store: store, transport: transport, nowSeconds: () => now,
        monotonicMillis: () => monotonic);

      await client.ActiveRevalidateAsync();
      monotonic = 59_000;             // still inside the window
      await client.ActiveRevalidateAsync();
      Assert.Equal(1, transport.ValidateCalls);

      monotonic = 61_000;             // window elapsed
      await client.ActiveRevalidateAsync();
      Assert.Equal(2, transport.ValidateCalls);
    }

    /// <summary>
    /// The debounce suppresses revalidation, so anchoring it to the wall clock
    /// let a backwards clock jump suppress revocation enforcement for the size
    /// of the jump. On a licensing SDK that is an adversarial move, not just an
    /// NTP correction: set the clock back and the app stops checking in.
    /// A monotonic source cannot be steered this way.
    /// </summary>
    [Fact]
    public async Task ActiveRevalidateAsync_debounce_is_immune_to_a_backwards_wall_clock() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = lease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = now });

      long wallClock = now;
      long monotonic = 0;
      var transport = new CountingConfirmTransport(lease);
      var client = new KeylightClient(
        config, store: store, transport: transport, nowSeconds: () => wallClock,
        monotonicMillis: () => monotonic);

      await client.ActiveRevalidateAsync();
      Assert.Equal(1, transport.ValidateCalls);

      // User winds the wall clock back a day; real time still moves forward.
      wallClock = now - 86_400;
      monotonic = 61_000;

      await client.ActiveRevalidateAsync();
      Assert.Equal(2, transport.ValidateCalls);
    }

    [Fact]
    public async Task ActiveRevalidateAsync_debounce_is_in_memory_and_does_not_survive_a_new_client() {
      // The debounce must NOT be persisted: a process restart (new client over
      // the same store) revalidates immediately.
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = lease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = now });

      var transport = new CountingConfirmTransport(lease);

      var first = ClientHelper.MakeClient(config, transport, store, now);
      await first.ActiveRevalidateAsync();

      var second = ClientHelper.MakeClient(config, transport, store, now);
      await second.ActiveRevalidateAsync();

      Assert.Equal(2, transport.ValidateCalls);
    }

    // ─── (3) bypasses the staleness gates ──────────────────────────────────

    [Fact]
    public async Task ActiveRevalidateAsync_forces_validate_where_RefreshIfNeededAsync_skips() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();
      // Fetched 1 second ago — inside RefreshIfNeededAsync's 5-minute debounce
      // and nowhere near the 6 h staleness / 24 h near-expiry thresholds.
      store.Save(new CachedState { Lease = lease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = now - 1 });

      var transport = new CountingConfirmTransport(lease);
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.RefreshIfNeededAsync();
      Assert.Equal(0, transport.ValidateCalls); // sanity: the gates do skip

      await client.ActiveRevalidateAsync();
      Assert.Equal(1, transport.ValidateCalls);
      Assert.Equal(KeylightState.Licensed, client.State);
    }

    // ─── (4) definitive rejection downgrades immediately ───────────────────

    [Fact]
    public async Task ActiveRevalidateAsync_definitive_rejection_downgrades() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = lease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = now });

      var transport = new RevokedTransport();
      var client = ClientHelper.MakeClient(config, transport, store, now);

      Assert.Equal(KeylightState.Licensed, client.State); // sanity before

      await client.ActiveRevalidateAsync();

      Assert.Equal(1, transport.ValidateCalls);
      Assert.Equal(KeylightState.Invalid, client.State);
      Assert.False(client.HasEntitlement("pro"));
    }

    [Fact]
    public async Task ActiveRevalidateAsync_real_422_revoke_downgrades() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = lease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = now });

      var transport = new Real422RevokedTransport();
      var client = ClientHelper.MakeClient(config, transport, store, now);

      Assert.Equal(KeylightState.Licensed, client.State); // sanity before

      await client.ActiveRevalidateAsync();

      Assert.Equal(1, transport.ValidateCalls);
      Assert.Equal(KeylightState.Invalid, client.State);
      Assert.False(client.HasEntitlement("pro"));
    }

    // ─── (5) transient failure never downgrades ────────────────────────────

    [Fact]
    public async Task ActiveRevalidateAsync_transient_network_failure_keeps_state() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = lease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = now });

      var transport = new ThrowingValidateTransport();
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.ActiveRevalidateAsync();

      Assert.Equal(1, transport.ValidateCalls);
      Assert.Equal(KeylightState.Licensed, client.State);
      Assert.True(client.HasEntitlement("pro"));
    }

    [Fact]
    public async Task ActiveRevalidateAsync_never_throws_on_lease_verification_failure() {
      var (activeLease, trustedKeys, now) = Vectors.Get("valid-active");
      var (tamperedLease, _, _) = Vectors.Get("tampered-entitlements");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = activeLease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = now });

      var transport = new TamperedLeaseTransport(tamperedLease);
      var client = ClientHelper.MakeClient(config, transport, store, now);

      // Must not propagate: an active-use trigger fires from UI code paths and
      // a bad server payload must never crash the host app or downgrade it.
      await client.ActiveRevalidateAsync();

      Assert.Equal(1, transport.ValidateCalls);
      Assert.Equal(KeylightState.Licensed, client.State);
    }
  }
}
