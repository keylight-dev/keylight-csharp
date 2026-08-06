using System.Threading;
using System.Threading.Tasks;
using Keylight;
using Xunit;

namespace Keylight.Tests {

  /// <summary>
  /// The wire shape being right is not the same thing as the client actually
  /// filling it in — that distinction is what let the missing `license_key`
  /// ship green. These assert the KeylightClient populates `cpu_cores` and
  /// `memory` on the real activate and validate paths.
  /// </summary>
  public class CapabilityTelemetryClientTests {

    private const string Key = "KL-TEST-AAAA-BBBB";

    private class CapturingTransport : IKeylightTransport {
      public ActivateRequest? LastActivate;
      public ValidateRequest? LastValidate;
      private readonly Lease _lease;
      public CapturingTransport(Lease lease) { _lease = lease; }

      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default) {
        LastActivate = req;
        return Task.FromResult(new ActivateResponse { Activated = true, InstanceId = "i-1", Lease = _lease });
      }

      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default) {
        LastValidate = req;
        return Task.FromResult(new ValidateResponse { Valid = true, Lease = _lease });
      }

      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
        => Task.CompletedTask;
    }

    private static (KeylightClient client, CapturingTransport transport) Make() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var transport = new CapturingTransport(lease);
      return (ClientHelper.MakeClient(config, transport, new MemoryLeaseStore(), now), transport);
    }

    [Fact]
    public async Task Activate_reports_the_cpu_core_bucket() {
      var (client, transport) = Make();
      await client.ActivateAsync(Key);
      Assert.Equal(Device.CpuCores, transport.LastActivate!.CpuCores);
      Assert.NotNull(transport.LastActivate!.CpuCores);
    }

    [Fact]
    public async Task Activate_reports_the_memory_bucket() {
      var (client, transport) = Make();
      await client.ActivateAsync(Key);
      // Device.Memory may legitimately be null where the probe cannot run; the
      // requirement is that the client forwards whatever the probe produced and
      // never substitutes a placeholder.
      Assert.Equal(Device.Memory, transport.LastActivate!.Memory);
    }

    [Fact]
    public async Task Validate_reports_both_buckets() {
      var (client, transport) = Make();
      await client.ActivateAsync(Key);
      await client.ValidateAsync();
      Assert.Equal(Device.CpuCores, transport.LastValidate!.CpuCores);
      Assert.Equal(Device.Memory, transport.LastValidate!.Memory);
    }
  }
}
