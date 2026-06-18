using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Keylight;
using Xunit;

namespace Keylight.Tests {

  public class KeyPrefixTests {

    // A FakeTransport that counts how many times ActivateAsync is called.
    private class CountingTransport : IKeylightTransport {
      public int ActivateCalls { get; private set; }
      private readonly Func<ActivateRequest, ActivateResponse> _activate;

      public CountingTransport(Func<ActivateRequest, ActivateResponse> activate) {
        _activate = activate;
      }

      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default) {
        ActivateCalls++;
        return Task.FromResult(_activate(req));
      }

      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default)
        => Task.FromResult(new ValidateResponse { Valid = false });

      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
        => Task.CompletedTask;
    }

    [Fact]
    public async Task ActivateAsync_wrong_prefix_throws_ActivationException_without_calling_transport() {
      var config = KeylightConfig
        .Builder("tenant1", "product1", "sdk-key-test")
        .KeyPrefix("ACME")
        .Build();

      var store = new MemoryLeaseStore();
      var transport = new CountingTransport(_ => new ActivateResponse { Activated = true });
      var client = new KeylightClient(config, store: store, transport: transport, nowSeconds: () => 0L);

      var ex = await Assert.ThrowsAsync<ActivationException>(
        () => client.ActivateAsync("WRONG-123"));

      Assert.Contains("ACME", ex.Message);
      Assert.Equal(0, transport.ActivateCalls);
    }

    [Fact]
    public async Task ActivateAsync_correct_prefix_does_not_throw_on_prefix_check() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = KeylightConfig
        .Builder("tenant1", "product1", "sdk-key-test")
        .TrustedKeys(trustedKeys)
        .KeyPrefix("ACME")
        .Build();

      var store = new MemoryLeaseStore();
      var transport = new CountingTransport(_ => new ActivateResponse {
        Activated  = true,
        InstanceId = "inst-001",
        Lease      = lease
      });
      var client = new KeylightClient(config, store: store, transport: transport, nowSeconds: () => now);

      // Should not throw on the prefix check; transport must be called exactly once.
      await client.ActivateAsync("ACME-VALID-KEY");

      Assert.Equal(1, transport.ActivateCalls);
      Assert.Equal(KeylightState.Licensed, client.State);
    }
  }
}
