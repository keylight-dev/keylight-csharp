using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Keylight;
using Xunit;

namespace Keylight.Tests {

  /// <summary>
  /// Installs activated under an SDK build that never persisted the license key
  /// cannot check in: /validate requires `license_key` on the wire and the key
  /// is unrecoverable locally (the cached lease carries only a hash). Such a
  /// device never refreshes its lease, so once the cached lease passes its 7-day
  /// expiry it drops to Expired permanently — stranding a paying customer, and
  /// delaying any revocation by the same window.
  ///
  /// ValidateAsync skips the call silently (it is a hot path and must not
  /// throw), so the condition has to be observable some other way for a host app
  /// to be able to recover by re-prompting for the key.
  /// </summary>
  public class NeedsReactivationTests {

    private class UnusedTransport : IKeylightTransport {
      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => throw new System.NotSupportedException();
      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default)
        => throw new System.NotSupportedException();
      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
        => Task.CompletedTask;
    }

    private class CountingTransport : IKeylightTransport {
      public int ValidateCalls;
      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => throw new System.NotSupportedException();
      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default) {
        ValidateCalls++;
        return Task.FromResult(new ValidateResponse { Valid = true });
      }
      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
        => Task.CompletedTask;
    }

    [Fact]
    public void A_legacy_install_with_no_stored_key_reports_NeedsReactivation() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var store = new MemoryLeaseStore();
      // Exactly what an SDK build predating the LicenseKey field left on disk.
      store.Save(new CachedState { Lease = lease, InstanceId = "inst-001", FetchedAt = now });

      var client = ClientHelper.MakeClient(
        ClientHelper.MakeConfig(trustedKeys), new UnusedTransport(), store, now);

      Assert.True(client.NeedsReactivation);
    }

    [Fact]
    public void A_current_install_with_a_stored_key_does_not() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var store = new MemoryLeaseStore();
      store.Save(new CachedState {
        Lease = lease, InstanceId = "inst-001", LicenseKey = "KL-TEST-AAAA-BBBB", FetchedAt = now
      });

      var client = ClientHelper.MakeClient(
        ClientHelper.MakeConfig(trustedKeys), new UnusedTransport(), store, now);

      Assert.False(client.NeedsReactivation);
    }

    /// <summary>
    /// A trial-only device has no key because it never activated — it is not
    /// stranded and must not be told to re-enter one it does not have.
    /// </summary>
    [Fact]
    public void A_trial_only_device_does_not_report_NeedsReactivation() {
      var (_, trustedKeys, now) = Vectors.Get("valid-active");
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { TrialStartedAt = now, FetchedAt = now });

      var client = ClientHelper.MakeClient(
        ClientHelper.MakeConfig(trustedKeys), new UnusedTransport(), store, now);

      Assert.False(client.NeedsReactivation);
    }

    [Fact]
    public void A_fresh_install_with_an_empty_store_does_not() {
      var (_, trustedKeys, now) = Vectors.Get("valid-active");
      var client = ClientHelper.MakeClient(
        ClientHelper.MakeConfig(trustedKeys), new UnusedTransport(), new MemoryLeaseStore(), now);

      Assert.False(client.NeedsReactivation);
    }

    /// <summary>
    /// Pins the behavior the property exists to explain: the stranded install
    /// makes no network call, so nothing downstream can notice it on its own.
    /// </summary>
    [Fact]
    public async Task A_stranded_install_makes_no_validate_call() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = lease, InstanceId = "inst-001", FetchedAt = now });

      var transport = new CountingTransport();
      var client = ClientHelper.MakeClient(
        ClientHelper.MakeConfig(trustedKeys), transport, store, now);

      await client.ValidateAsync();

      Assert.Equal(0, transport.ValidateCalls);
      Assert.True(client.NeedsReactivation);
    }

    /// <summary>
    /// Re-activating is the documented recovery, so it must actually clear the
    /// condition — otherwise the app would prompt in a loop.
    /// </summary>
    [Fact]
    public async Task Reactivating_clears_the_condition() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = lease, InstanceId = "inst-001", FetchedAt = now });

      var transport = new ReactivateTransport(lease);
      var client = ClientHelper.MakeClient(
        ClientHelper.MakeConfig(trustedKeys), transport, store, now);

      Assert.True(client.NeedsReactivation);

      await client.ActivateAsync("KL-TEST-AAAA-BBBB");

      Assert.False(client.NeedsReactivation);
    }

    private class ReactivateTransport : IKeylightTransport {
      private readonly Lease _lease;
      public ReactivateTransport(Lease lease) { _lease = lease; }
      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => Task.FromResult(new ActivateResponse {
             Activated = true, Lease = _lease, InstanceId = "inst-001"
           });
      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default)
        => throw new System.NotSupportedException();
      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
        => Task.CompletedTask;
    }
  }
}
