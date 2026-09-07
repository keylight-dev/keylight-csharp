using System.Collections.Generic;
using Keylight;
using Xunit;

namespace Keylight.Tests {

  /// <summary>
  /// <c>EffectiveFreeTierEnabled()</c> mirrors <c>EffectiveTrialDurationDays()</c>:
  /// the server value wins when the install has heard one, and the fallback is
  /// <c>false</c> because <see cref="KeylightConfig"/> carries no free-tier seed.
  /// </summary>
  public class EffectiveFreeTierEnabledTests {

    private const long T = 1_700_000_000L;

    private static KeylightClient Client(MemoryLeaseStore store) {
      var config = ClientHelper.MakeConfig(new Dictionary<string, string>());
      var transport = new FakeTransport(_ => new ActivateResponse { Activated = false });
      return ClientHelper.MakeClient(config, transport, store, T);
    }

    [Fact]
    public void Nothing_stored_reports_false() {
      Assert.False(Client(new MemoryLeaseStore()).EffectiveFreeTierEnabled());
    }

    [Fact]
    public void Stored_state_without_a_server_value_reports_false() {
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { FetchedAt = T, TrialStartedAt = T });
      Assert.False(Client(store).EffectiveFreeTierEnabled());
    }

    [Fact]
    public void Server_true_wins() {
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { FetchedAt = T, ProductFreeTierEnabled = true });
      Assert.True(Client(store).EffectiveFreeTierEnabled());
    }

    [Fact]
    public void Server_false_is_a_value_not_an_absence() {
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { FetchedAt = T, ProductFreeTierEnabled = false });
      Assert.False(Client(store).EffectiveFreeTierEnabled());
    }

    [Fact]
    public void Absorbed_config_updates_the_answer() {
      var store = new MemoryLeaseStore();
      var client = Client(store);
      Assert.False(client.EffectiveFreeTierEnabled());

      client.AbsorbConfigFields(new ProductConfigFields { FreeTierEnabled = true });
      Assert.True(client.EffectiveFreeTierEnabled());

      // A response that omits the field leaves the last value in place.
      client.AbsorbConfigFields(new ProductConfigFields { TrialDurationDays = 7 });
      Assert.True(client.EffectiveFreeTierEnabled());
      Assert.Equal(7, client.EffectiveTrialDurationDays());
    }
  }
}
