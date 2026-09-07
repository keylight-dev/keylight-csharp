using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Keylight;
using Keylight.Crypto;
using Xunit;

namespace Keylight.Tests {

  /// <summary>
  /// <c>RequireSignedConfig</c> — the enforcement half of server-owned settings.
  /// </summary>
  /// <remarks>
  /// Two rules from section 4 of
  /// keylight-cpp/docs/superpowers/specs/2026-09-05-trial-parity-handoff.md are what these
  /// tests exist to hold:
  ///
  /// <b>Rule 2:</b> a config that does not verify is never cached. It falls back
  /// to the seed, never to what the server claimed.
  ///
  /// <b>Rule 4:</b> the rule applies wherever the fields ride. If /config
  /// verifies but a validate body caches unsigned settings, the check is one
  /// route away from useless. The routes are /config, validate, and the keyless
  /// beacon; the beacon's half is covered by
  /// <see cref="KeylessBeaconTests.With_RequireSignedConfig_an_unsigned_beacon_reply_is_not_cached"/>,
  /// and all three land in the same <c>AbsorbConfigFields</c> gate.
  /// </remarks>
  public class ConfigEnforcementTests {

    private const long T = 1_700_000_000L;
    private const string TenantId = "tenant1";
    private const string ProductId = "product1";
    private const string Kid = "k1";

    // The SDK's Ed25519 is verify-only, as it should be — there is nothing to
    // sign with at runtime. Signed material is therefore pre-generated, the same
    // way conformance/vectors.json is. Reproduce with:
    //
    //   seed = bytes 0x01..0x20, PKCS#8-wrapped; sign the payload below.
    //
    // Payload shape (frozen): cfg1|kid|tenant|product|issuedAt|expiresAt|days|freeTier
    private const string PublicKeyB64 = "ebVWLo/mVPlAeLES6KmLp5AfhTrmlb7X4OORC60ElmQ=";
    private const long IssuedAt = T;
    private const long ExpiresAt = T + 86_400;

    /// cfg1|k1|tenant1|product1|1700000000|1700086400|14|false
    private const string SigTrial14 =
      "B5/chgdhpY9WtGlx2kpsUMiRtK4jHUn3Z/DkfD5t5xtGnCJ2FpnnptAvtTtd3xtuU21DW0fXKB0cpYKLaq7uAw==";
    /// cfg1|k1|tenant1|product1|1700000000|1700086400|21|false
    private const string SigTrial21 =
      "CboICVh2TI3MgPz1tuKkJ40EbaOGQHk+QVegesgiUt6hJ4vNRQ5KSAAI+UOHMeRXnCsF5jPgpWdduuyvEusGCg==";
    /// cfg1|k1|tenant1|neighbourapp|1700000000|1700086400|14|false
    private const string SigNeighbour14 =
      "P+A3q7XX5DPs1HdihL8OpvJTFEg4QgBk6M32t+TIy9vdEp5KbACAD/ke2say51xF6HXN6eC2N6mkzIc2JI5vAg==";

    private class ConfigTransport : IKeylightTransport, IKeylightConfigTransport {
      private readonly ConfigResponse? _config;
      private readonly ValidateResponse? _validate;

      public ConfigTransport(ConfigResponse? config = null, ValidateResponse? validate = null) {
        _config = config;
        _validate = validate;
      }

      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => Task.FromResult(new ActivateResponse { Activated = false });
      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default)
        => Task.FromResult(_validate ?? new ValidateResponse { Valid = false, Error = "no license" });
      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
        => Task.CompletedTask;
      public Task<ConfigResponse?> FetchConfigAsync(CancellationToken ct = default)
        => Task.FromResult(_config);
    }

    private KeylightClient Client(
        MemoryLeaseStore store, IKeylightTransport transport,
        bool requireSigned, int? seedDays = null, string trustedKid = Kid, long now = T) {
      var b = KeylightConfig.Builder(TenantId, ProductId, "sdk-key-test")
        .TrustedKeys(new Dictionary<string, string> { [trustedKid] = PublicKeyB64 })
        .RequireSignedConfig(requireSigned);
      if (seedDays.HasValue) b = b.TrialDurationDays(seedDays.Value);
      return new KeylightClient(b.Build(), store: store, transport: transport, nowSeconds: () => now);
    }

    private static ConfigSignature Sig(string signature) =>
      new ConfigSignature {
        IssuedAt = IssuedAt, ExpiresAt = ExpiresAt, Kid = Kid, Signature = signature
      };

    private ConfigResponse Cfg(int days, bool freeTier = false, ConfigSignature? signature = null)
      => new ConfigResponse {
        TrialDurationDays = days, FreeTierEnabled = freeTier, ConfigSignature = signature
      };

    // ─── default off ────────────────────────────────────────────────────────

    [Fact]
    public void Enforcement_is_off_by_default() {
      var config = KeylightConfig.Builder(TenantId, ProductId, "sdk-key-test").Build();
      Assert.False(config.RequireSignedConfig);
    }

    [Fact]
    public async Task Unsigned_config_is_absorbed_when_enforcement_is_off() {
      var store = new MemoryLeaseStore();
      var client = Client(store, new ConfigTransport(Cfg(14)), requireSigned: false);

      await client.CheckOnLaunchAsync();

      Assert.Equal(14, client.EffectiveTrialDurationDays());
    }

    // ─── rule 2: never cache what did not verify ────────────────────────────

    [Fact]
    public async Task Unsigned_config_is_rejected_when_enforcement_is_on() {
      var store = new MemoryLeaseStore();
      var client = Client(store, new ConfigTransport(Cfg(365)), requireSigned: true, seedDays: 7);

      await client.CheckOnLaunchAsync();

      Assert.Equal(7, client.EffectiveTrialDurationDays());
    }

    [Fact]
    public async Task Validly_signed_config_is_absorbed_when_enforcement_is_on() {
      var store = new MemoryLeaseStore();
      var client = Client(
        store, new ConfigTransport(Cfg(14, signature: Sig(SigTrial14))), requireSigned: true, seedDays: 7);

      await client.CheckOnLaunchAsync();

      Assert.Equal(14, client.EffectiveTrialDurationDays());
    }

    [Fact]
    public async Task Tampered_config_is_rejected_when_enforcement_is_on() {
      var store = new MemoryLeaseStore();
      // Signature is valid — for 14 days. The body claims 365.
      var client = Client(
        store, new ConfigTransport(Cfg(365, signature: Sig(SigTrial14))), requireSigned: true, seedDays: 7);

      await client.CheckOnLaunchAsync();

      Assert.Equal(7, client.EffectiveTrialDurationDays());
    }

    /// Rotation degrades to frozen settings rather than breakage.
    [Fact]
    public async Task Config_signed_by_a_rotated_away_kid_is_rejected() {
      var store = new MemoryLeaseStore();
      var client = Client(
        store, new ConfigTransport(Cfg(14, signature: Sig(SigTrial14))),
        requireSigned: true, seedDays: 7, trustedKid: "k2");

      await client.CheckOnLaunchAsync();

      Assert.Equal(7, client.EffectiveTrialDurationDays());
    }

    [Fact]
    public async Task Config_signed_for_another_product_is_rejected() {
      var store = new MemoryLeaseStore();
      var client = Client(
        store, new ConfigTransport(Cfg(14, signature: Sig(SigNeighbour14))),
        requireSigned: true, seedDays: 7);

      await client.CheckOnLaunchAsync();

      Assert.Equal(7, client.EffectiveTrialDurationDays());
    }

    // ─── rule 4: validate is not a way around the gate ──────────────────────

    /// The bypass this design exists to prevent: if validate wrote the cache
    /// without checking, stripping the signature from that one route would be
    /// enough to set any trial length you liked.
    [Fact]
    public async Task Unsigned_validate_body_cannot_write_the_cache_when_enforcement_is_on() {
      var store = new MemoryLeaseStore();
      var validate = new ValidateResponse {
        Valid = true, TrialDurationDays = 365, FreeTierEnabled = true
      };
      // Without a stored licence CheckOnLaunchAsync never calls validate, and the
      // body under test would never be seen at all.
      store.Save(new CachedState {
        LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = T
      });
      var client = Client(
        store, new ConfigTransport(validate: validate), requireSigned: true, seedDays: 7);

      await client.CheckOnLaunchAsync();

      Assert.Equal(7, client.EffectiveTrialDurationDays());
    }

    [Fact]
    public async Task Signed_validate_body_is_accepted_when_enforcement_is_on() {
      var store = new MemoryLeaseStore();
      var validate = new ValidateResponse {
        Valid = true, TrialDurationDays = 21, FreeTierEnabled = false,
        ConfigSignature = Sig(SigTrial21)
      };
      // Without a stored licence CheckOnLaunchAsync never calls validate, and the
      // body under test would never be seen at all.
      store.Save(new CachedState {
        LicenseKey = "KL-TEST-AAAA-BBBB", InstanceId = "inst-001", FetchedAt = T
      });
      var client = Client(
        store, new ConfigTransport(validate: validate), requireSigned: true, seedDays: 7);

      await client.CheckOnLaunchAsync();

      Assert.Equal(21, client.EffectiveTrialDurationDays());
    }
  }
}
