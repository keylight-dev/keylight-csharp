using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Keylight;
using Xunit;

namespace Keylight.Tests {

  // ─── tests ───────────────────────────────────────────────────────────────────

  public class ClientTests {

    [Fact]
    public async Task ActivateAsync_with_valid_active_lease_sets_Licensed_state() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store  = new MemoryLeaseStore();
      var transport = new FakeTransport(_ => new ActivateResponse {
        Activated  = true,
        InstanceId = "inst-001",
        Lease      = lease
      });
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.ActivateAsync("TEST-LICENSE-KEY");

      Assert.Equal(KeylightState.Licensed, client.State);
    }

    [Fact]
    public async Task ActivateAsync_with_valid_active_lease_enables_entitlements() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store  = new MemoryLeaseStore();
      var transport = new FakeTransport(_ => new ActivateResponse {
        Activated  = true,
        InstanceId = "inst-001",
        Lease      = lease
      });
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.ActivateAsync("TEST-LICENSE-KEY");

      Assert.True(client.HasEntitlement("pro"));
    }

    [Fact]
    public async Task ActivateAsync_with_tampered_lease_throws_LeaseVerificationFailedException() {
      var (lease, trustedKeys, now) = Vectors.Get("tampered-entitlements");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store  = new MemoryLeaseStore();
      var transport = new FakeTransport(_ => new ActivateResponse {
        Activated  = true,
        InstanceId = "inst-001",
        Lease      = lease
      });
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await Assert.ThrowsAsync<LeaseVerificationFailedException>(
        () => client.ActivateAsync("TEST-LICENSE-KEY"));
    }

    [Fact]
    public async Task ActivateAsync_with_expired_lease_sets_Expired_state() {
      // expired-beyond-skew: expiresAt=1781681046, now=1781681446 (400s past = beyond 300s skew)
      var (lease, trustedKeys, now) = Vectors.Get("expired-beyond-skew");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store  = new MemoryLeaseStore();
      var transport = new FakeTransport(_ => new ActivateResponse {
        Activated  = true,
        InstanceId = "inst-001",
        Lease      = lease
      });
      // expired-beyond-skew: signature is valid but lease.expired = true.
      // verifyOrReject only checks IsTrusted (kidKnown+signatureValid), NOT expiry.
      // So activation succeeds but State should reflect Expired.
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.ActivateAsync("TEST-LICENSE-KEY");

      Assert.Equal(KeylightState.Expired, client.State);
    }

    [Fact]
    public async Task DeactivateAsync_clears_cached_lease() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store  = new MemoryLeaseStore();
      var transport = new FakeTransport(_ => new ActivateResponse {
        Activated  = true,
        InstanceId = "inst-001",
        Lease      = lease
      });
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.ActivateAsync("TEST-LICENSE-KEY");
      Assert.Equal(KeylightState.Licensed, client.State);

      await client.DeactivateAsync();

      Assert.Equal(KeylightState.Invalid, client.State);
      Assert.False(client.HasEntitlement("pro"));
    }

    [Fact]
    public async Task ValidateAsync_updates_cached_lease() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store  = new MemoryLeaseStore();

      // Start with an existing cached state (simulating prior activation)
      store.Save(new CachedState {
        Lease = lease,
        InstanceId = "inst-001",
        FetchedAt = now
      });

      var transport = new FakeTransport(
        activate: _ => new ActivateResponse { Activated = true },
        validate: _ => new ValidateResponse { Valid = true, Lease = lease }
      );
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.ValidateAsync();

      Assert.Equal(KeylightState.Licensed, client.State);
    }

    [Fact]
    public void Sync_Activate_works() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store  = new MemoryLeaseStore();
      var transport = new FakeTransport(_ => new ActivateResponse {
        Activated  = true,
        InstanceId = "inst-001",
        Lease      = lease
      });
      var client = ClientHelper.MakeClient(config, transport, store, now);

      client.Activate("TEST-LICENSE-KEY");

      Assert.Equal(KeylightState.Licensed, client.State);
    }
  }
}
