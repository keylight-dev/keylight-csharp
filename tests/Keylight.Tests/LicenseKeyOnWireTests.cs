using System.Threading;
using System.Threading.Tasks;
using Keylight;
using Xunit;

namespace Keylight.Tests {

  /// <summary>
  /// The worker requires <c>license_key</c> on BOTH /validate and /deactivate
  /// (worker/src/routes/validate.ts:32, deactivate.ts:25 — both
  /// <c>z.string().min(1)</c>). Omitting it is a hard 400 `invalid-body`, not a
  /// dropped field, so every check-in fails and the SDK degrades to
  /// activate-only: revocation can never land and the lease is never refreshed.
  ///
  /// The key therefore has to survive activation in the store, the way the
  /// Swift (keychain), Rust (cached_license_key) and JS (LICENSE_KEY) SDKs
  /// already do.
  /// </summary>
  public class LicenseKeyOnWireTests {

    private const string Key = "KL-TEST-AAAA-BBBB";

    /// Captures the request objects the client hands to the transport.
    private class CapturingTransport : IKeylightTransport {
      public ValidateRequest? LastValidate;
      public DeactivateRequest? LastDeactivate;
      private readonly Lease _lease;
      public CapturingTransport(Lease lease) { _lease = lease; }

      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => Task.FromResult(new ActivateResponse { Activated = true, InstanceId = "i-1", Lease = _lease });

      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default) {
        LastValidate = req;
        return Task.FromResult(new ValidateResponse { Valid = true, Lease = _lease });
      }

      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default) {
        LastDeactivate = req;
        return Task.CompletedTask;
      }
    }

    private static (KeylightClient client, CapturingTransport transport, MemoryLeaseStore store, long now) Make() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();
      var transport = new CapturingTransport(lease);
      return (ClientHelper.MakeClient(config, transport, store, now), transport, store, now);
    }

    [Fact]
    public async Task Validate_sends_the_license_key() {
      var (client, transport, _, _) = Make();
      await client.ActivateAsync(Key);

      await client.ValidateAsync();

      Assert.NotNull(transport.LastValidate);
      Assert.Equal(Key, transport.LastValidate!.LicenseKey);
    }

    [Fact]
    public async Task Deactivate_sends_the_license_key() {
      var (client, transport, _, _) = Make();
      await client.ActivateAsync(Key);

      await client.DeactivateAsync();

      Assert.NotNull(transport.LastDeactivate);
      Assert.Equal(Key, transport.LastDeactivate!.LicenseKey);
    }

    [Fact]
    public void Validate_request_serializes_license_key() {
      var json = new ValidateRequest { LicenseKey = Key, InstanceId = "i-1" }.ToJson();
      Assert.Contains("\"license_key\"", json);
      Assert.Contains(Key, json);
    }

    [Fact]
    public void Deactivate_request_serializes_license_key() {
      var json = new DeactivateRequest { LicenseKey = Key, InstanceId = "i-1" }.ToJson();
      Assert.Contains("\"license_key\"", json);
      Assert.Contains(Key, json);
    }

    /// Installs that activated before the key was persisted have a cached lease
    /// but no key. Sending an empty one is a guaranteed 400, so the call must be
    /// skipped and the last-known-good state left intact — the same "never
    /// downgrade on something transient" rule the rest of the client follows.
    [Fact]
    public async Task Validate_is_a_no_op_when_no_key_was_persisted() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store = new MemoryLeaseStore();
      // Deliberately NO LicenseKey — this is the pre-upgrade install.
      store.Save(new CachedState { Lease = lease, InstanceId = "i-legacy", FetchedAt = now });
      var transport = new CapturingTransport(lease);
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.ValidateAsync();

      Assert.Null(transport.LastValidate);
      Assert.Equal(KeylightState.Licensed, client.State);
    }
  }
}
