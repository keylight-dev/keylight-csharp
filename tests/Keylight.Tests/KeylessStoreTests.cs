using System.Threading.Tasks;
using Keylight;
using Xunit;

namespace Keylight.Tests {
  public class KeylessStoreTests {
    [Fact]
    public void Four_keyless_fields_round_trip_through_json() {
      var s = new CachedState {
        FetchedAt = 1, FreeTierInstanceId = "ft-1", KeylessLastState = "trial",
        LastKeylessPingAt = 1_700_000_000L, CachedHardwareId = "hw-1"
      };
      var back = FileLeaseStore.DeserializeCachedState(FileLeaseStore.SerializeCachedState(s))!;
      Assert.Equal("ft-1", back.FreeTierInstanceId);
      Assert.Equal("trial", back.KeylessLastState);
      Assert.Equal(1_700_000_000L, back.LastKeylessPingAt);
      Assert.Equal("hw-1", back.CachedHardwareId);
    }

    [Fact]
    public void Absent_keyless_fields_read_back_as_null() {
      var back = FileLeaseStore.DeserializeCachedState("{\"fetchedAt\":1}")!;
      Assert.Null(back.FreeTierInstanceId);
      Assert.Null(back.KeylessLastState);
      Assert.Null(back.LastKeylessPingAt);
      Assert.Null(back.CachedHardwareId);
    }

    [Fact]
    public async Task Validate_rebuilds_carry_the_keyless_fields() {
      var (lease, keys, now) = Vectors.Get("valid-active");
      var store = new MemoryLeaseStore();
      store.Save(new CachedState {
        Lease = lease, InstanceId = "inst", LicenseKey = "KEY-1", FetchedAt = now,
        FreeTierInstanceId = "ft-1", KeylessLastState = "trial", LastKeylessPingAt = now - 10, CachedHardwareId = "hw-1"
      });
      var transport = new FakeTransport(
        activate: _ => new ActivateResponse { Activated = false },
        validate: _ => new ValidateResponse { Valid = true, Lease = lease });
      var client = new KeylightClient(ClientHelper.MakeConfig(keys), store, transport, () => now, null, new FakeDevice("hw-1"));
      await client.ValidateAsync();
      var after = store.Load()!;
      Assert.Equal("ft-1", after.FreeTierInstanceId);
      Assert.Equal("trial", after.KeylessLastState);
      Assert.Equal(now - 10, after.LastKeylessPingAt);
      Assert.Equal("hw-1", after.CachedHardwareId);
    }

    [Fact]
    public async Task Deactivate_keeps_trial_start_and_keyless_fields_but_drops_the_license() {
      var (lease, keys, now) = Vectors.Get("valid-active");
      var store = new MemoryLeaseStore();
      store.Save(new CachedState {
        Lease = lease, InstanceId = "inst", LicenseKey = "KEY-1", FetchedAt = now, TrialStartedAt = now - 100,
        FreeTierInstanceId = "ft-1", CachedHardwareId = "hw-1", ProductTrialDurationDays = 14
      });
      var client = ClientHelper.MakeClient(ClientHelper.MakeConfig(keys),
        new FakeTransport(_ => new ActivateResponse { Activated = false }), store, now);
      await client.DeactivateAsync();
      var after = store.Load()!;
      Assert.Null(after.Lease);
      Assert.Null(after.LicenseKey);
      Assert.Null(after.InstanceId);
      Assert.Equal(now - 100, after.TrialStartedAt);
      Assert.Equal("ft-1", after.FreeTierInstanceId);
      Assert.Equal("hw-1", after.CachedHardwareId);
      Assert.Equal(14, after.ProductTrialDurationDays);
    }
  }
}
