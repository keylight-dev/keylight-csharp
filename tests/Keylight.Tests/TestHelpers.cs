using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Keylight;

namespace Keylight.Tests {

  // ─── in-memory store ──────────────────────────────────────────────────────

  public class MemoryLeaseStore : ILeaseStore {
    private CachedState? _state;
    public CachedState? Load() => _state;
    public void Save(CachedState s) => _state = s;
    public void Clear() => _state = null;
  }

  // ─── fake transport ────────────────────────────────────────────────────────

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

  public class KeylessTransport : IKeylightTransport, IKeylightKeylessTransport {
    public readonly List<KeylessRequest> Beacons = new();
    public Func<KeylessRequest, KeylessResponse?> Reply = _ => new KeylessResponse { Received = true };
    public Exception? Throw; // set to make the beacon fail

    public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
      => Task.FromResult(new ActivateResponse { Activated = false });
    public Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default)
      => Task.FromResult(new ValidateResponse { Valid = false, Error = "no license" });
    public Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default) => Task.CompletedTask;

    public Task<KeylessResponse?> ReportKeylessAsync(KeylessRequest req, CancellationToken ct = default) {
      if (Throw != null) throw Throw;
      Beacons.Add(req);
      return Task.FromResult(Reply(req));
    }
  }

  public class FakeDevice : IDeviceIdentity {
    private readonly Queue<string?> _answers;
    public FakeDevice(params string?[] answers) { _answers = new Queue<string?>(answers); }
    public string? HardwareId() => _answers.Count > 1 ? _answers.Dequeue() : (_answers.Count == 1 ? _answers.Peek() : null);
  }

  // ─── config / client builder helpers ──────────────────────────────────────

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

  // ─── conformance vector loader ─────────────────────────────────────────────

  static class Vectors {
    // Raw DTOs — match the JSON structure in conformance/vectors.json.
    public class Vec {
      public string name { get; set; } = "";
      public Lease lease { get; set; } = new Lease();
      public Dictionary<string, string> trustedKeys { get; set; } = new();
      public long now { get; set; }
    }

    public class VecFile {
      public int skewSeconds { get; set; }
      public List<Vec> vectors { get; set; } = new();
    }

    private static readonly VecFile _file = LoadFile();

    private static VecFile LoadFile() {
      var path = Path.Combine(AppContext.BaseDirectory, "conformance", "vectors.json");
      var json = System.IO.File.ReadAllText(path);
      return JsonSerializer.Deserialize<VecFile>(json,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    /// <summary>
    /// Returns the lease, trustedKeys, and now timestamp for a named vector.
    /// </summary>
    public static (Lease lease, Dictionary<string, string> trustedKeys, long now) Get(string name) {
      foreach (var v in _file.vectors)
        if (v.name == name) return (v.lease, v.trustedKeys, v.now);
      throw new InvalidOperationException($"Vector '{name}' not found");
    }
  }
}
