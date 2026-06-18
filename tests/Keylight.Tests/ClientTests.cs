using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Keylight;
using Xunit;

namespace Keylight.Tests {

  // ─── in-memory store ────────────────────────────────────────────────────────

  public class MemoryLeaseStore : ILeaseStore {
    private CachedState? _state;
    public CachedState? Load() => _state;
    public void Save(CachedState s) => _state = s;
    public void Clear() => _state = null;
  }

  // ─── conformance vector loader ──────────────────────────────────────────────

  static class Vectors {
    private class Vec {
      public string name { get; set; } = "";
      public Lease lease { get; set; } = new Lease();
      public Dictionary<string, string> trustedKeys { get; set; } = new();
      public long now { get; set; }
    }
    private class VecFile {
      public int skewSeconds { get; set; }
      public List<Vec> vectors { get; set; } = new();
    }

    private static readonly VecFile _file = Load();
    private static VecFile Load() {
      var path = Path.Combine(AppContext.BaseDirectory, "conformance", "vectors.json");
      var json = File.ReadAllText(path);
      return JsonSerializer.Deserialize<VecFile>(json,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public static (Lease lease, Dictionary<string, string> trustedKeys, long now) Get(string name) {
      foreach (var v in _file.vectors)
        if (v.name == name) return (v.lease, v.trustedKeys, v.now);
      throw new InvalidOperationException($"Vector '{name}' not found");
    }
  }

  // ─── fake transport ──────────────────────────────────────────────────────────

  public class FakeTransport : IKeylightTransport {
    private readonly Func<ActivateRequest, ActivateResponse> _activate;
    private readonly Func<ValidateRequest, ValidateResponse>? _validate;

    public FakeTransport(
      Func<ActivateRequest, ActivateResponse> activate,
      Func<ValidateRequest, ValidateResponse>? validate = null) {
      _activate = activate;
      _validate = validate;
    }

    public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
      => Task.FromResult(_activate(req));

    public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default) {
      if (_validate == null) return Task.FromResult(new ValidateResponse { Valid = false, Error = "no validate" });
      return Task.FromResult(_validate(req));
    }

    public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
      => Task.CompletedTask;
  }

  // ─── helper builder ─────────────────────────────────────────────────────────

  static class ClientHelper {
    public static KeylightConfig MakeConfig(Dictionary<string, string> trustedKeys)
      => KeylightConfig
        .Builder("tenant1", "product1", "sdk-key-test")
        .TrustedKeys(trustedKeys)
        .Build();

    public static KeylightClient MakeClient(
      KeylightConfig config,
      IKeylightTransport transport,
      ILeaseStore store,
      long nowSeconds)
      => new KeylightClient(config, store: store, transport: transport, nowSeconds: () => nowSeconds);
  }

  // ─── tests ───────────────────────────────────────────────────────────────────

  public class ClientTests {

    [Fact]
    public async Task ActivateAsync_with_valid_active_lease_sets_Licensed_state() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store  = new MemoryLeaseStore();
      var transport = new FakeTransport(_ => new ActivateResponse {
        Activated  = true,
        InstanceId = "inst-001",
        Lease      = lease
      });
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.ActivateAsync("TEST-LICENSE-KEY");

      Assert.Equal(KeylightState.Licensed, client.State);
    }

    [Fact]
    public async Task ActivateAsync_with_valid_active_lease_enables_entitlements() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store  = new MemoryLeaseStore();
      var transport = new FakeTransport(_ => new ActivateResponse {
        Activated  = true,
        InstanceId = "inst-001",
        Lease      = lease
      });
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.ActivateAsync("TEST-LICENSE-KEY");

      Assert.True(client.HasEntitlement("pro"));
    }

    [Fact]
    public async Task ActivateAsync_with_tampered_lease_throws_LeaseVerificationFailedException() {
      var (lease, trustedKeys, now) = Vectors.Get("tampered-entitlements");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store  = new MemoryLeaseStore();
      var transport = new FakeTransport(_ => new ActivateResponse {
        Activated  = true,
        InstanceId = "inst-001",
        Lease      = lease
      });
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await Assert.ThrowsAsync<LeaseVerificationFailedException>(
        () => client.ActivateAsync("TEST-LICENSE-KEY"));
    }

    [Fact]
    public async Task ActivateAsync_with_expired_lease_sets_Expired_state() {
      // expired-beyond-skew: expiresAt=1781681046, now=1781681446 (400s past = beyond 300s skew)
      var (lease, trustedKeys, now) = Vectors.Get("expired-beyond-skew");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store  = new MemoryLeaseStore();
      var transport = new FakeTransport(_ => new ActivateResponse {
        Activated  = true,
        InstanceId = "inst-001",
        Lease      = lease
      });
      // expired-beyond-skew: signature is valid but lease.expired = true.
      // verifyOrReject only checks IsTrusted (kidKnown+signatureValid), NOT expiry.
      // So activation succeeds but State should reflect Expired.
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.ActivateAsync("TEST-LICENSE-KEY");

      Assert.Equal(KeylightState.Expired, client.State);
    }

    [Fact]
    public async Task DeactivateAsync_clears_cached_lease() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store  = new MemoryLeaseStore();
      var transport = new FakeTransport(_ => new ActivateResponse {
        Activated  = true,
        InstanceId = "inst-001",
        Lease      = lease
      });
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.ActivateAsync("TEST-LICENSE-KEY");
      Assert.Equal(KeylightState.Licensed, client.State);

      await client.DeactivateAsync();

      Assert.Equal(KeylightState.Invalid, client.State);
      Assert.False(client.HasEntitlement("pro"));
    }

    [Fact]
    public async Task ValidateAsync_updates_cached_lease() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store  = new MemoryLeaseStore();

      // Start with an existing cached state (simulating prior activation)
      store.Save(new CachedState {
        Lease = lease,
        InstanceId = "inst-001",
        FetchedAt = now
      });

      var transport = new FakeTransport(
        activate: _ => new ActivateResponse { Activated = true },
        validate: _ => new ValidateResponse { Valid = true, Lease = lease }
      );
      var client = ClientHelper.MakeClient(config, transport, store, now);

      await client.ValidateAsync();

      Assert.Equal(KeylightState.Licensed, client.State);
    }

    [Fact]
    public void Sync_Activate_works() {
      var (lease, trustedKeys, now) = Vectors.Get("valid-active");
      var config = ClientHelper.MakeConfig(trustedKeys);
      var store  = new MemoryLeaseStore();
      var transport = new FakeTransport(_ => new ActivateResponse {
        Activated  = true,
        InstanceId = "inst-001",
        Lease      = lease
      });
      var client = ClientHelper.MakeClient(config, transport, store, now);

      client.Activate("TEST-LICENSE-KEY");

      Assert.Equal(KeylightState.Licensed, client.State);
    }
  }
}
