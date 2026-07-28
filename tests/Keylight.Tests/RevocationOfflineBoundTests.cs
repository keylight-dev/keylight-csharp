using System;
using System.Threading;
using System.Threading.Tasks;
using Keylight;
using Xunit;

namespace Keylight.Tests {

  /// <summary>
  /// Cross-SDK revocation &amp; offline-bound parity fixes:
  ///   1. CheckOnLaunchAsync always performs a server ValidateAsync round-trip
  ///      (no longer gated by RefreshIfNeededAsync's staleness debounce).
  ///   2. ValidateAsync reads resp.Valid; a definitive rejection (Valid==false,
  ///      no lease) maps to KeylightState.Invalid instead of being swallowed.
  ///   3. MaxOfflineDays default 7 -&gt; 15, and State (not just HasEntitlement)
  ///      is gated by it.
  /// See docs/superpowers/specs/2026-07-08-cross-sdk-revocation-parity-design.md
  /// in the keylight repo for the full design.
  /// </summary>
  public class RevocationOfflineBoundTests {

    // A transport whose ValidateAsync throws, simulating a transient network
    // failure (timeout / DNS / 5xx surfaced as an exception by the transport).
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

    // A transport whose ValidateAsync returns a definitive rejection
    // (Valid == false, no lease) — the wire shape a revoked/deactivated
    // instance gets back from the server.
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

    // A transport whose ValidateAsync always confirms the same lease is
    // still valid — used to assert CheckOnLaunchAsync makes the round-trip
    // even when the cache is fresh (no staleness reason to call the server).
    private class ConfirmingTransport : IKeylightTransport {
      public int ValidateCalls;
      private readonly Lease _lease;
      public ConfirmingTransport(Lease lease) { _lease = lease; }
      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => throw new NotSupportedException();
      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default) {
        ValidateCalls++;
        return Task.FromResult(new ValidateResponse { Valid = true, Lease = _lease });
      }
      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
        => Task.CompletedTask;
    }

    // ─── (a) revoke caught on launch ───────────────────────────────────────

    [Fact]
    public async Task CheckOnLaunchAsync_catches_revocation_even_when_cache_is_fresh() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();

      // Cache was refreshed 1 second ago — well within the 5-minute debounce
      // that RefreshIfNeededAsync would normally honor. Pre-fix, CheckOnLaunchAsync
      // delegated to RefreshIfNeededAsync and would skip the server entirely here.
      store.Save(new CachedState { Lease = lease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = now - 1 });

      var transport = new RevokedTransport();
      var client = ClientHelper.MakeClient(config, transport, store, now);

      // Sanity: before the launch check, the cached lease still reads Licensed.
      Assert.Equal(KeylightState.Licensed, client.State);

      await client.CheckOnLaunchAsync();

      Assert.Equal(1, transport.ValidateCalls);
      Assert.Equal(KeylightState.Invalid, client.State);
    }

    // ─── (b) transient failure keeps access within cap ─────────────────────

    [Fact]
    public async Task CheckOnLaunchAsync_transient_failure_keeps_licensed_within_cap() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = lease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = now - 1 });

      var transport = new ThrowingValidateTransport();
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.CheckOnLaunchAsync();

      Assert.Equal(1, transport.ValidateCalls);
      // A transient/thrown failure must never mutate state — last-known-good wins.
      Assert.Equal(KeylightState.Licensed, client.State);
      Assert.True(client.HasEntitlement("pro"));
    }

    // ─── (c) offline past cap denies ────────────────────────────────────────

    [Fact]
    public void State_denies_when_last_validated_online_exceeds_offline_cap() {
      var (lease, trustedKeys, vectorNow) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys); // default MaxOfflineDays == 15
      var store = new MemoryLeaseStore();

      // FetchedAt (last successful online validation) is 20 days before "now".
      // The lease's own ExpiresAt is far beyond "now" — it is NOT naturally
      // expired — so any denial can only come from the offline-day gate.
      long fetchedAt = vectorNow - 20L * 86400L;
      store.Save(new CachedState { Lease = lease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = fetchedAt });

      var client = ClientHelper.MakeClient(config, new FakeTransport(_ => new ActivateResponse()), store, vectorNow);

      Assert.NotEqual(KeylightState.Licensed, client.State);
      Assert.False(client.HasEntitlement("pro"));
    }

    // ─── (d) cap disabled never denies for age ─────────────────────────────

    [Fact]
    public void State_ignores_offline_age_when_cap_disabled() {
      var (lease, trustedKeys, vectorNow) = Vectors.Get("valid-active");
      var config = KeylightConfig
        .Builder("tenant1", "product1", "sdk-key-test")
        .TrustedKeys(trustedKeys)
        .MaxOfflineDays(0) // 0 == disabled, per existing GetCachedTrustedLease convention
        .Build();
      var store = new MemoryLeaseStore();

      long fetchedAt = vectorNow - 400L * 86400L; // over a year offline
      store.Save(new CachedState { Lease = lease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = fetchedAt });

      var client = ClientHelper.MakeClient(config, new FakeTransport(_ => new ActivateResponse()), store, vectorNow);

      Assert.Equal(KeylightState.Licensed, client.State);
      Assert.True(client.HasEntitlement("pro"));
    }

    // ─── (e) definitive Valid:false invalidates (the swallow fix) ──────────

    [Fact]
    public async Task ValidateAsync_definitive_rejection_sets_Invalid() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = lease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = now });

      var transport = new RevokedTransport();
      var client = ClientHelper.MakeClient(config, transport, store, now);

      Assert.Equal(KeylightState.Licensed, client.State); // sanity before

      await client.ValidateAsync();

      Assert.Equal(KeylightState.Invalid, client.State);
      Assert.False(client.HasEntitlement("pro"));
    }

    [Fact]
    public async Task ValidateAsync_transport_exception_does_not_mutate_state() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = lease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = now });

      var transport = new ThrowingValidateTransport();
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.ValidateAsync();

      Assert.Equal(KeylightState.Licensed, client.State);
    }

    // ─── (f) real worker 422 revoke shape actually clears the lease ────────
    //
    // RevokedTransport above returns a *parsed* ValidateResponse{Valid=false},
    // which never exercises the real bug: HttpClientTransport.ValidateAsync
    // throws ActivationException for ANY non-2xx status (including 422) before
    // Keylight.cs ever sees a ValidateResponse. The real worker's revoke /
    // deactivated-instance response is HTTP 422, body `{"error":"..."}`, no
    // lease, no top-level "valid" field — which used to land in the generic
    // `catch { return; }` no-op path and leave a revoked license "Licensed"
    // until its own (still-valid) lease expired on its own schedule.

    // A transport that throws the exact shape HttpClientTransport surfaces for
    // a real 422 revoke: ActivationException(422, ..., body) with no lease and
    // no "valid" field in the body.
    private class Real422RevokedTransport : IKeylightTransport {
      public int ValidateCalls;
      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => throw new NotSupportedException();
      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default) {
        ValidateCalls++;
        const string body = "{\"error\":\"Instance not found or deactivated\"}";
        throw new ActivationException(422, $"Keylight API returned HTTP 422: {body}", body);
      }
      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
        => Task.CompletedTask;
    }

    // A transport that throws the same real 422 shape but WITH a lease attached
    // (status "expired") — the worker still vouches for a lease, just a lesser
    // one. This must be kept, not cleared: State resolves to Expired off the
    // returned lease rather than Invalid.
    private class Real422ExpiredLeaseTransport : IKeylightTransport {
      public int ValidateCalls;
      private readonly Lease _lease;
      public Real422ExpiredLeaseTransport(Lease lease) { _lease = lease; }
      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => throw new NotSupportedException();
      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default) {
        ValidateCalls++;
        var leaseJson = Keylight.Json.JsonCodec.StringifyValue(WireHelpers.LeaseToJsonValue(_lease));
        var body = $"{{\"error\":\"expired\",\"lease\":{leaseJson}}}";
        throw new ActivationException(422, $"Keylight API returned HTTP 422: {body}", body);
      }
      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
        => Task.CompletedTask;
    }

    [Fact]
    public async Task ValidateAsync_real_422_revoke_with_no_lease_clears_stale_lease() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = lease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = now });

      var transport = new Real422RevokedTransport();
      var client = ClientHelper.MakeClient(config, transport, store, now);

      Assert.Equal(KeylightState.Licensed, client.State); // sanity before

      await client.ValidateAsync();

      Assert.Equal(1, transport.ValidateCalls);
      Assert.Equal(KeylightState.Invalid, client.State);
      Assert.False(client.HasEntitlement("pro"));
    }

    [Fact]
    public async Task CheckOnLaunchAsync_real_422_revoke_with_no_lease_denies() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = lease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = now - 1 });

      var transport = new Real422RevokedTransport();
      var client = ClientHelper.MakeClient(config, transport, store, now);

      Assert.Equal(KeylightState.Licensed, client.State); // sanity before

      await client.CheckOnLaunchAsync();

      Assert.Equal(1, transport.ValidateCalls);
      Assert.Equal(KeylightState.Invalid, client.State);
      Assert.False(client.HasEntitlement("pro"));
    }

    [Fact]
    public async Task ValidateAsync_real_422_with_expired_lease_keeps_lease_resolves_expired() {
      var (activeLease, trustedKeys, now) = Vectors.Get("valid-active");
      var (expiredLease, _, _) = Vectors.Get("expired-status");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = activeLease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = now });

      var transport = new Real422ExpiredLeaseTransport(expiredLease);
      var client = ClientHelper.MakeClient(config, transport, store, now);

      Assert.Equal(KeylightState.Licensed, client.State); // sanity before

      await client.ValidateAsync();

      Assert.Equal(1, transport.ValidateCalls);
      // The 422 carried a lease (status "expired"), so it must be kept and
      // resolved via the normal lease-status path — NOT treated as a bare
      // revoke and cleared to Invalid.
      Assert.Equal(KeylightState.Expired, client.State);
    }

    // ─── always-validate-on-launch regression guard ────────────────────────

    [Fact]
    public async Task CheckOnLaunchAsync_always_calls_server_even_when_not_stale() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();
      // FetchedAt just 1 second ago: RefreshIfNeededAsync's own debounce (5 min)
      // would skip this. CheckOnLaunchAsync must not delegate to it any more.
      store.Save(new CachedState { Lease = lease, LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = now - 1 });

      var transport = new ConfirmingTransport(lease);
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.CheckOnLaunchAsync();

      Assert.Equal(1, transport.ValidateCalls);
      Assert.Equal(KeylightState.Licensed, client.State);
    }
  }
}
