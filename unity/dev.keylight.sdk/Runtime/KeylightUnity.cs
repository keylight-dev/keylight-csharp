#if UNITY_2021_3_OR_NEWER
using UnityEngine;

namespace Keylight.Unity {
  /// <summary>
  /// Convenience factory that wires up a <see cref="KeylightClient"/> for
  /// Unity: uses <see cref="UnityLeaseStore"/> for persistence, stamps
  /// <c>Application.platform.ToString()</c> as the <c>platform</c> field,
  /// and uses <see cref="UnityWebRequestTransport"/> so the package works
  /// on all Unity platforms — including <strong>WebGL</strong>, where
  /// <c>HttpClient</c> is unavailable.
  ///
  /// Usage (MonoBehaviour):
  /// <code>
  /// var client = KeylightUnity.CreateClient(
  ///   KeylightConfig.Builder("tenant", "product", "sdk-key")
  ///     .TrustedKeys(new Dictionary&lt;string, string&gt; { ["kid"] = "hex-pubkey" })
  ///     .Build());
  /// await client.CheckOnLaunchAsync();
  /// </code>
  /// </summary>
  public static class KeylightUnity {
    /// <summary>
    /// Creates a fully configured <see cref="KeylightClient"/> using:
    /// <list type="bullet">
    ///   <item><see cref="UnityLeaseStore"/> backed by <c>Application.persistentDataPath</c></item>
    ///   <item><see cref="UnityWebRequestTransport"/> — works on ALL platforms including WebGL</item>
    /// </list>
    /// The <c>platform</c> string sent to the server is set to
    /// <c>Application.platform.ToString()</c> (e.g. "WindowsPlayer", "IPhonePlayer", "WebGLPlayer").
    /// </summary>
    /// <param name="config">A fully built <see cref="KeylightConfig"/>.</param>
    /// <param name="leaseFilename">
    ///   Optional filename for the JSON lease file (default: <c>keylight-lease.json</c>).
    /// </param>
    public static KeylightClient CreateClient(
      KeylightConfig config,
      string leaseFilename = "keylight-lease.json") {

      var store     = new UnityLeaseStore(leaseFilename);
      var platform  = Application.platform.ToString();
      var transport = new UnityPlatformTransport(config, platform);
      return new KeylightClient(config, store, transport);
    }
  }

  /// <summary>
  /// Internal transport that wraps <see cref="UnityWebRequestTransport"/> and
  /// stamps the Unity runtime platform string onto every outbound
  /// activate/validate request.
  /// </summary>
  internal sealed class UnityPlatformTransport : IKeylightTransport {
    private readonly UnityWebRequestTransport _inner;
    private readonly string _platform;

    internal UnityPlatformTransport(KeylightConfig config, string platform) {
      _inner = new UnityWebRequestTransport(
        config.BaseUrl, config.TenantId, config.ProductId, config.SdkKey);
      _platform = platform;
    }

    public async System.Threading.Tasks.Task<ActivateResponse> ActivateAsync(
      ActivateRequest req,
      System.Threading.CancellationToken ct = default) {
      req.Platform = _platform;
      return await _inner.ActivateAsync(req, ct).ConfigureAwait(false);
    }

    public async System.Threading.Tasks.Task<ValidateResponse> ValidateAsync(
      ValidateRequest req,
      System.Threading.CancellationToken ct = default) {
      req.Platform = _platform;
      return await _inner.ValidateAsync(req, ct).ConfigureAwait(false);
    }

    public System.Threading.Tasks.Task DeactivateAsync(
      DeactivateRequest req,
      System.Threading.CancellationToken ct = default)
      => _inner.DeactivateAsync(req, ct);
  }
}
#endif // UNITY_2021_3_OR_NEWER
