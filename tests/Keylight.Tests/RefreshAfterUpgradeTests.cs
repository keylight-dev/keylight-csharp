using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Keylight;
using Xunit;

namespace Keylight.Tests {

  /// <summary>
  /// Parity with <c>refreshAfterUpgrade</c> in the Swift, JS, and Rust SDKs: poll
  /// validate until the stored license's entitlements or resolved state differ
  /// from the snapshot taken at call time.
  ///
  ///   1. No stored license → false, and nothing is sent.
  ///   2. An entitlement change on a later poll → true.
  ///   3. A state change (a definitive rejection counts) → true.
  ///   4. Timeout → false, and the call never runs past it.
  ///   5. Cancellation → false.
  ///   6. A transient validate failure is swallowed; a later change still → true.
  /// </summary>
  public class RefreshAfterUpgradeTests {

    private const long T = 1_700_000_000L;

    // Leases signed with the same throwaway key ConfigEnforcementTests uses
    // (seed = bytes 0x01..0x20). The SDK's Ed25519 is verify-only, so the
    // bytes are pre-generated. Payload shape: v3|kid|hash|inst|iat|exp|status|ents
    private const string PublicKeyB64 = "ebVWLo/mVPlAeLES6KmLp5AfhTrmlb7X4OORC60ElmQ=";

    private static Lease LeaseWith(string[] entitlements, string signature) => new Lease {
      Kid = "k1", LicenseKeyHash = "hash-abc", InstanceId = "inst-001",
      IssuedAt = T, ExpiresAt = T + 7 * 86_400, Status = "active",
      Entitlements = entitlements, Signature = signature
    };

    /// v3|k1|hash-abc|inst-001|1700000000|1700604800|active|pro
    private static Lease ProLease() => LeaseWith(new[] { "pro" },
      "MRjMsvKCREsQ+rWMLG3BdFPF50uQNpe8dyZjDPpBTneeR2frQn3darDY/oF78HshVjKoDD1Xxg05ZtvLXFHBBA==");

    /// v3|k1|hash-abc|inst-001|1700000000|1700604800|active|pro,team
    private static Lease ProTeamLease() => LeaseWith(new[] { "pro", "team" },
      "6mlNRsv7BGDgu35Ej7fgijAFPO64fIV8XF9964joVVFqHfAVXXymTi1vftt/wx1qWj5z85wQyg7OK1p2bzWnDA==");

    /// <summary>Answers each validate call from a script, repeating the last
    /// entry once the script runs out. An entry may be a response or a thrown
    /// exception.</summary>
    private class ScriptedTransport : IKeylightTransport {
      private readonly List<Func<ValidateResponse>> _script;
      public int ValidateCalls;
      public ScriptedTransport(params Func<ValidateResponse>[] script) { _script = new List<Func<ValidateResponse>>(script); }

      public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => throw new NotSupportedException();
      public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default) {
        var i = Math.Min(ValidateCalls, _script.Count - 1);
        ValidateCalls++;
        return Task.FromResult(_script[i]());
      }
      public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
        => Task.CompletedTask;
    }

    private static Func<ValidateResponse> Confirm(Lease lease) =>
      () => new ValidateResponse { Valid = true, Lease = lease };
    private static readonly Func<ValidateResponse> Revoked =
      () => new ValidateResponse { Valid = false, Lease = null, Error = "revoked" };
    private static readonly Func<ValidateResponse> NetworkDown =
      () => throw new Exception("network unreachable");

    private static KeylightClient LicensedClient(IKeylightTransport transport, MemoryLeaseStore? store = null) {
      var config = ClientHelper.MakeConfig(new Dictionary<string, string> { ["k1"] = PublicKeyB64 });
      store ??= new MemoryLeaseStore();
      store.Save(new CachedState {
        Lease = ProLease(), InstanceId = "inst-001", LicenseKey = "TEST-LICENSE-KEY", FetchedAt = T
      });
      var client = ClientHelper.MakeClient(config, transport, store, T + 60);
      Assert.Equal(KeylightState.Licensed, client.State); // precondition
      return client;
    }

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task No_stored_license_returns_false_without_a_network_call() {
      var transport = new ScriptedTransport(Confirm(ProTeamLease()));
      var config = ClientHelper.MakeConfig(new Dictionary<string, string> { ["k1"] = PublicKeyB64 });
      var client = ClientHelper.MakeClient(config, transport, new MemoryLeaseStore(), T);

      var changed = await client.RefreshAfterUpgradeAsync(Generous, Poll);

      Assert.False(changed);
      Assert.Equal(0, transport.ValidateCalls);
    }

    [Fact]
    public async Task Trial_only_state_with_no_license_key_returns_false_without_a_network_call() {
      // Stored, but never activated: ValidateAsync would no-op on the missing
      // key, so polling could only ever time out.
      var transport = new ScriptedTransport(Confirm(ProTeamLease()));
      var config = ClientHelper.MakeConfig(new Dictionary<string, string> { ["k1"] = PublicKeyB64 });
      var store = new MemoryLeaseStore();
      store.Save(new CachedState { FetchedAt = T, TrialStartedAt = T });
      var client = ClientHelper.MakeClient(config, transport, store, T);

      var changed = await client.RefreshAfterUpgradeAsync(Generous, Poll);

      Assert.False(changed);
      Assert.Equal(0, transport.ValidateCalls);
    }

    [Fact]
    public async Task Entitlement_change_on_second_poll_returns_true() {
      var transport = new ScriptedTransport(Confirm(ProLease()), Confirm(ProTeamLease()));
      var client = LicensedClient(transport);

      var changed = await client.RefreshAfterUpgradeAsync(Generous, Poll);

      Assert.True(changed);
      Assert.Equal(2, transport.ValidateCalls);
      Assert.True(client.HasEntitlement("team"));
      Assert.Equal(KeylightState.Licensed, client.State);
    }

    [Fact]
    public async Task State_change_returns_true() {
      // A definitive rejection is still "something happened".
      var transport = new ScriptedTransport(Revoked);
      var client = LicensedClient(transport);

      var changed = await client.RefreshAfterUpgradeAsync(Generous, Poll);

      Assert.True(changed);
      Assert.Equal(1, transport.ValidateCalls);
      Assert.Equal(KeylightState.Invalid, client.State);
    }

    [Fact]
    public async Task Nothing_changes_returns_false_on_timeout_and_never_runs_past_it() {
      var transport = new ScriptedTransport(Confirm(ProLease()));
      var client = LicensedClient(transport);
      var timeout = TimeSpan.FromMilliseconds(350);

      var sw = System.Diagnostics.Stopwatch.StartNew();
      var changed = await client.RefreshAfterUpgradeAsync(timeout, Poll);
      sw.Stop();

      Assert.False(changed);
      Assert.True(transport.ValidateCalls >= 2, $"expected repeated polls, got {transport.ValidateCalls}");
      // 350 ms is not a multiple of the 100 ms poll: the final delay must be
      // capped to the remainder rather than overshooting to 400 ms.
      Assert.True(sw.ElapsedMilliseconds < 350 + 500, $"ran past the timeout: {sw.ElapsedMilliseconds} ms");
      Assert.Equal(KeylightState.Licensed, client.State);
    }

    [Fact]
    public async Task Poll_interval_is_floored_at_100ms() {
      var transport = new ScriptedTransport(Confirm(ProLease()));
      var client = LicensedClient(transport);

      var changed = await client.RefreshAfterUpgradeAsync(TimeSpan.FromMilliseconds(250), TimeSpan.Zero);

      Assert.False(changed);
      // Validate-first at 100 ms per poll: a 250 ms window fits at most four
      // looks (0, 100, 200, and one after the capped final delay); a zero
      // interval would have spun hundreds of times.
      Assert.InRange(transport.ValidateCalls, 1, 4);
    }

    [Fact]
    public async Task Cancellation_returns_false() {
      var transport = new ScriptedTransport(Confirm(ProLease()));
      var client = LicensedClient(transport);
      using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

      var changed = await client.RefreshAfterUpgradeAsync(Generous, Poll, cts.Token);

      Assert.False(changed);
      Assert.True(cts.IsCancellationRequested);
      Assert.Equal(KeylightState.Licensed, client.State);
    }

    [Fact]
    public async Task Already_cancelled_token_returns_false_without_a_network_call() {
      var transport = new ScriptedTransport(Confirm(ProTeamLease()));
      var client = LicensedClient(transport);
      using var cts = new CancellationTokenSource();
      cts.Cancel();

      var changed = await client.RefreshAfterUpgradeAsync(Generous, Poll, cts.Token);

      Assert.False(changed);
      Assert.Equal(0, transport.ValidateCalls);
    }

    [Fact]
    public async Task Transient_error_then_change_returns_true() {
      var transport = new ScriptedTransport(NetworkDown, NetworkDown, Confirm(ProTeamLease()));
      var client = LicensedClient(transport);

      var changed = await client.RefreshAfterUpgradeAsync(Generous, Poll);

      Assert.True(changed);
      Assert.Equal(3, transport.ValidateCalls);
      Assert.True(client.HasEntitlement("team"));
    }
  }
}
