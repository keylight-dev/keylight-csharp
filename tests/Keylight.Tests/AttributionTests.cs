using System.Threading.Tasks;
using Keylight;
using Xunit;

namespace Keylight.Tests {
  public class AttributionTests {
    private const long T = 1_700_000_000L;

    private class RecordingTransport : IKeylightTransport, IKeylightKeylessTransport {
      public ActivateRequest? LastActivate; public ValidateRequest? LastValidate;
      private readonly Lease _lease;
      public RecordingTransport(Lease lease) { _lease = lease; }
      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, System.Threading.CancellationToken ct = default) {
        LastActivate = req; return Task.FromResult(new ActivateResponse { Activated = true, InstanceId = "inst", Lease = _lease });
      }
      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, System.Threading.CancellationToken ct = default) {
        LastValidate = req; return Task.FromResult(new ValidateResponse { Valid = true, Lease = _lease });
      }
      public Task DeactivateAsync(DeactivateRequest req, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
      public Task<KeylessResponse?> ReportKeylessAsync(KeylessRequest req, System.Threading.CancellationToken ct = default)
        => Task.FromResult<KeylessResponse?>(new KeylessResponse { Received = true });
    }

    [Fact]
    public async Task Activate_carries_an_existing_free_tier_id_and_machine_hash() {
      var (lease, keys, now) = Vectors.Get("valid-active");
      var store = new MemoryLeaseStore();
      var t = new RecordingTransport(lease);
      var c = new KeylightClient(ClientHelper.MakeConfig(keys), store, t, () => now, null, new FakeDevice("hardware-1"));
      var ft = c.FreeTierInstanceId();
      await c.ActivateAsync("KEY-1");
      Assert.Equal(ft, t.LastActivate!.FreeTierInstanceId);
      Assert.Equal(c.MachineHash(), t.LastActivate!.MachineHash);
      Assert.Equal(ft, store.Load()!.FreeTierInstanceId); // survived the activate rebuild
    }

    [Fact]
    public async Task Activate_omits_free_tier_id_when_none_was_minted() {
      var (lease, keys, now) = Vectors.Get("valid-active");
      var t = new RecordingTransport(lease);
      var c = new KeylightClient(ClientHelper.MakeConfig(keys), new MemoryLeaseStore(), t, () => now, null, new FakeDevice("hardware-1"));
      await c.ActivateAsync("KEY-1");
      Assert.Null(t.LastActivate!.FreeTierInstanceId);
      Assert.NotNull(t.LastActivate!.MachineHash);
    }

    [Fact]
    public async Task Validate_carries_machine_hash() {
      var (lease, keys, now) = Vectors.Get("valid-active");
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { Lease = lease, InstanceId = "inst", LicenseKey = "KEY-1", FetchedAt = now });
      var t = new RecordingTransport(lease);
      var c = new KeylightClient(ClientHelper.MakeConfig(keys), store, t, () => now, null, new FakeDevice("hardware-1"));
      await c.ValidateAsync();
      Assert.Equal(c.MachineHash(), t.LastValidate!.MachineHash);
    }

    [Fact]
    public async Task Launch_trial_stamp_mints_the_free_tier_id() {
      var store = new MemoryLeaseStore();
      var t = new KeylessTransport();
      var c = new KeylightClient(KeylightConfig.Builder("testco", "testapp", "k").TrialDurationDays(14).Build(),
        store, t, () => T, null, new FakeDevice("hardware-1"));
      await c.CheckOnLaunchAsync();
      Assert.NotNull(store.Load()!.TrialStartedAt);
      Assert.NotNull(store.Load()!.FreeTierInstanceId);
    }
  }
}
