using System;
using System.Threading;
using System.Threading.Tasks;
using Keylight;
using Xunit;

namespace Keylight.Tests {

  /// <summary>
  /// The trial length is a setting the <b>server</b> owns; the value on
  /// <see cref="KeylightConfig"/> is only a seed for an install that has never
  /// reached the server.
  /// </summary>
  /// <remarks>
  /// Resolution order is server value → local seed → 0. These are the tests that
  /// caught real bugs in the C++ port rather than the ones that restate the
  /// implementation — see keylight-cpp's
  /// docs/superpowers/specs/2026-09-05-trial-parity-handoff.md.
  /// </remarks>
  public class ServerOwnedTrialConfigTests {

    private const long T = 1_700_000_000L;

    /// <summary>Transport with no license, optionally answering GET /config and
    /// optionally carrying the settings on a validate response. Records whether
    /// /config was asked for.</summary>
    private class ConfigTransport : IKeylightTransport, IKeylightConfigTransport {
      private readonly ConfigResponse? _config;
      private readonly ValidateResponse? _validate;
      private readonly bool _throwOnConfig;
      public int ConfigCalls { get; private set; }

      public ConfigTransport(ConfigResponse? config = null, ValidateResponse? validate = null, bool throwOnConfig = false) {
        _config = config;
        _validate = validate;
        _throwOnConfig = throwOnConfig;
      }

      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => Task.FromResult(new ActivateResponse { Activated = false });
      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default)
        => Task.FromResult(_validate ?? new ValidateResponse { Valid = false, Error = "no license" });
      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
        => Task.CompletedTask;

      public Task<ConfigResponse?> FetchConfigAsync(CancellationToken ct = default) {
        ConfigCalls++;
        if (_throwOnConfig) throw new InvalidOperationException("offline");
        return Task.FromResult(_config);
      }
    }

    /// <summary>A transport that does NOT implement IKeylightConfigTransport —
    /// the shape every existing custom transport and test double has.</summary>
    private class LegacyTransport : IKeylightTransport {
      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => Task.FromResult(new ActivateResponse { Activated = false });
      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default)
        => Task.FromResult(new ValidateResponse { Valid = false, Error = "no license" });
      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
        => Task.CompletedTask;
    }

    private static KeylightClient Client(
      MemoryLeaseStore store,
      IKeylightTransport transport,
      int? seedDays = null,
      long now = T) {
      var b = KeylightConfig.Builder("tenant1", "product1", "sdk-key-test");
      if (seedDays.HasValue) b = b.TrialDurationDays(seedDays.Value);
      return new KeylightClient(b.Build(), store: store, transport: transport, nowSeconds: () => now);
    }

    private static ConfigResponse Cfg(int? days = null, bool? freeTier = null)
      => new ConfigResponse { TrialDurationDays = days, FreeTierEnabled = freeTier };

    // ─── resolution ─────────────────────────────────────────────────────────

    /// The headline case: a tenant turns a trial on in the dashboard for a build
    /// that shipped with none.
    [Fact]
    public async Task Server_duration_grants_a_trial_when_the_build_configured_none() {
      var store = new MemoryLeaseStore();
      var client = Client(store, new ConfigTransport(Cfg(days: 14)));

      await client.CheckOnLaunchAsync();

      Assert.Equal(14, client.EffectiveTrialDurationDays());
      Assert.Equal(KeylightState.Trial, client.State);
    }

    /// The reverse direction, and the one a tenant actually notices.
    [Fact]
    public async Task Server_zero_turns_off_a_seed_enabled_trial() {
      var store = new MemoryLeaseStore();
      var client = Client(store, new ConfigTransport(Cfg(days: 0)), seedDays: 14);

      await client.CheckOnLaunchAsync();

      Assert.Equal(0, client.EffectiveTrialDurationDays());
      Assert.Equal(KeylightState.Invalid, client.State);
    }

    /// An absent config falls through to the seed, never to zero.
    [Fact]
    public async Task Absent_config_falls_through_to_the_seed_not_to_zero() {
      var store = new MemoryLeaseStore();
      var client = Client(store, new ConfigTransport(config: null), seedDays: 14);

      await client.CheckOnLaunchAsync();

      Assert.Equal(14, client.EffectiveTrialDurationDays());
      Assert.Equal(KeylightState.Trial, client.State);
    }

    /// No server value and no seed means no trial — not a silent default.
    [Fact]
    public async Task No_server_value_and_no_seed_is_zero() {
      var store = new MemoryLeaseStore();
      var client = Client(store, new ConfigTransport(config: null));

      await client.CheckOnLaunchAsync();

      Assert.Equal(0, client.EffectiveTrialDurationDays());
      Assert.Equal(KeylightState.Invalid, client.State);
    }

    // ─── the stamp ──────────────────────────────────────────────────────────

    /// The bug that made a dashboard-set trial do nothing. The clock is stamped
    /// even at a zero duration, so a later-arriving duration has something to
    /// measure from.
    [Fact]
    public async Task Launch_stamps_the_clock_even_when_no_trial_is_on_offer() {
      var store = new MemoryLeaseStore();
      var client = Client(store, new ConfigTransport(config: null));

      await client.CheckOnLaunchAsync();

      Assert.True(store.Load()!.TrialStartedAt.HasValue);
      Assert.Equal(KeylightState.Invalid, client.State); // the stamp grants nothing

      // The tenant enables a 14-day trial. The window runs from the stamp that
      // already exists.
      var later = Client(store, new ConfigTransport(Cfg(days: 14)), now: T + 3 * 86400);
      await later.CheckOnLaunchAsync();

      Assert.Equal(KeylightState.Trial, later.State);
      Assert.Equal(T, store.Load()!.TrialStartedAt!.Value); // not restarted
    }

    /// Otherwise the trial is farmable by reinstalling: enable it later and every
    /// dormant install gets a brand-new window.
    [Fact]
    public async Task An_old_stamp_is_honoured_never_restarted() {
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { FetchedAt = T, TrialStartedAt = T - 60 * 86400 });
      var client = Client(store, new ConfigTransport(Cfg(days: 14)));

      await client.CheckOnLaunchAsync();

      // Assert the duration landed first, or this passes for the wrong reason:
      // a duration of 0 also resolves to Invalid.
      Assert.Equal(14, client.EffectiveTrialDurationDays());
      Assert.Equal(KeylightState.Expired, client.State);
      Assert.Equal(T - 60 * 86400, store.Load()!.TrialStartedAt!.Value);
    }

    // ─── persistence ────────────────────────────────────────────────────────

    /// A relaunch reads the last known server settings, not the seed — including
    /// when the network is gone.
    [Fact]
    public async Task A_server_zero_survives_a_relaunch_as_zero() {
      var store = new MemoryLeaseStore();
      await Client(store, new ConfigTransport(Cfg(days: 0)), seedDays: 14).CheckOnLaunchAsync();

      var offline = Client(store, new ConfigTransport(throwOnConfig: true), seedDays: 14);
      await offline.CheckOnLaunchAsync();

      Assert.Equal(0, offline.EffectiveTrialDurationDays());
    }

    /// An older worker sends neither field. That must not wipe what this install
    /// already learned.
    [Fact]
    public async Task A_response_with_no_config_fields_leaves_the_cache_alone() {
      var store = new MemoryLeaseStore();
      await Client(store, new ConfigTransport(Cfg(days: 7, freeTier: true)), seedDays: 30).CheckOnLaunchAsync();

      var older = Client(store, new ConfigTransport(Cfg()), seedDays: 30);
      await older.CheckOnLaunchAsync();

      Assert.Equal(7, older.EffectiveTrialDurationDays());
      Assert.True(store.Load()!.ProductFreeTierEnabled);
    }

    /// Each field merges on its own — a response carrying only one must not blank
    /// the other.
    [Fact]
    public async Task A_partial_response_merges_rather_than_replaces() {
      var store = new MemoryLeaseStore();
      await Client(store, new ConfigTransport(Cfg(days: 7, freeTier: true)), seedDays: 30).CheckOnLaunchAsync();

      var partial = Client(store, new ConfigTransport(Cfg(days: 21)), seedDays: 30);
      await partial.CheckOnLaunchAsync();

      Assert.Equal(21, partial.EffectiveTrialDurationDays());
      Assert.True(store.Load()!.ProductFreeTierEnabled);
    }

    /// The settings survive the file round-trip with absence intact — a cached 0
    /// must come back as 0, and a never-heard field as null.
    [Fact]
    public void Cached_settings_round_trip_through_the_store_serializer() {
      var zero = FileLeaseStore.DeserializeCachedState(
        FileLeaseStore.SerializeCachedState(
          new CachedState { FetchedAt = T, ProductTrialDurationDays = 0, ProductFreeTierEnabled = false }))!;
      Assert.Equal(0, zero.ProductTrialDurationDays);
      Assert.False(zero.ProductFreeTierEnabled);

      var absent = FileLeaseStore.DeserializeCachedState(
        FileLeaseStore.SerializeCachedState(new CachedState { FetchedAt = T }))!;
      Assert.Null(absent.ProductTrialDurationDays);
      Assert.Null(absent.ProductFreeTierEnabled);
    }

    // ─── wire ───────────────────────────────────────────────────────────────

    /// The settings ride on validate too, so a licensed install stays current
    /// without the extra route.
    [Fact]
    public async Task Validate_carries_the_config_and_it_is_absorbed() {
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { FetchedAt = T, InstanceId = "inst-1", LicenseKey = "TEST-KEY" });
      var validate = new ValidateResponse { Valid = true, TrialDurationDays = 21, FreeTierEnabled = true };
      var client = Client(store, new ConfigTransport(validate: validate), seedDays: 30);

      await client.ValidateAsync();

      Assert.Equal(21, client.EffectiveTrialDurationDays());
      Assert.True(store.Load()!.ProductFreeTierEnabled);
    }

    /// A validate must not blank the settings it did not mention: the client
    /// rebuilds CachedState from scratch on this path, so a field left out of the
    /// rebuild is a field silently reset.
    [Fact]
    public async Task Validate_without_config_fields_does_not_reset_the_cache() {
      var store = new MemoryLeaseStore();
      await Client(store, new ConfigTransport(Cfg(days: 7)), seedDays: 30).CheckOnLaunchAsync();

      store.Save(new CachedState {
        FetchedAt = T, InstanceId = "inst-1", LicenseKey = "TEST-KEY",
        ProductTrialDurationDays = store.Load()!.ProductTrialDurationDays,
      });
      var client = Client(store, new ConfigTransport(validate: new ValidateResponse { Valid = false }), seedDays: 30);

      await client.ValidateAsync();

      Assert.Equal(7, client.EffectiveTrialDurationDays());
    }

    // ─── transport compatibility ────────────────────────────────────────────

    /// A transport predating this feature keeps working: no fetch is attempted,
    /// nothing throws, and the seed governs.
    [Fact]
    public async Task A_transport_without_the_config_capability_still_works() {
      var store = new MemoryLeaseStore();
      var client = Client(store, new LegacyTransport(), seedDays: 14);

      await client.CheckOnLaunchAsync();

      Assert.Equal(14, client.EffectiveTrialDurationDays());
      Assert.Equal(KeylightState.Trial, client.State);
    }

    /// An unlicensed install fetches /config at launch, and this SDK is the only
    /// one that does.
    ///
    /// The others absorb the settings from the keyless beacon; this one has no
    /// beacon, so /config is the only route that ever reaches a trial user.
    /// Without the fetch the dashboard setting would never reach exactly the
    /// population it exists for. A licensed install skips it — ValidateAsync
    /// already carried the settings on a call it was making anyway.
    [Fact]
    public async Task An_unlicensed_install_fetches_config_because_it_has_no_other_carrier() {
      var store = new MemoryLeaseStore();
      var transport = new ConfigTransport(Cfg(days: 14));
      var client = Client(store, transport, seedDays: 14);

      await client.CheckOnLaunchAsync();

      Assert.Equal(1, transport.ConfigCalls);
    }

    /// A /config call that throws leaves the cached settings alone rather than
    /// surfacing into the host's launch path.
    [Fact]
    public async Task A_failing_config_fetch_never_throws() {
      var store = new MemoryLeaseStore();
      var client = Client(store, new ConfigTransport(throwOnConfig: true), seedDays: 14);

      await client.CheckOnLaunchAsync(); // must not throw

      Assert.Equal(14, client.EffectiveTrialDurationDays());
    }

    // ─── telemetry ──────────────────────────────────────────────────────────

    /// The diagnostic field reports the configured seed, not the effective value:
    /// echoing the server's own number back diagnoses nothing.
    [Fact]
    public void The_seed_is_reported_not_the_effective_value() {
      var json = new ValidateRequest {
        LicenseKey = "K", InstanceId = "i", SdkTrialDurationDays = 30
      }.ToJson();

      Assert.Contains("\"sdk_trial_duration_days\":30", json);
    }

    /// Omitted when the build configured no trial at all, rather than sent as a
    /// misleading 0.
    [Fact]
    public void The_seed_field_is_omitted_when_unconfigured() {
      var json = new ValidateRequest { LicenseKey = "K", InstanceId = "i" }.ToJson();

      Assert.DoesNotContain("sdk_trial_duration_days", json);
    }
  }
}
