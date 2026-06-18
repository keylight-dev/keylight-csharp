#if UNITY_2021_3_OR_NEWER
using System;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Keylight.Unity {
  /// <summary>
  /// An <see cref="IKeylightTransport"/> implemented with
  /// <see cref="UnityWebRequest"/> instead of <c>HttpClient</c>.
  ///
  /// <para>
  /// <c>HttpClient</c> is unavailable on WebGL (WASM has no threading) and can
  /// behave unpredictably on some mobile platforms. <c>UnityWebRequest</c> is
  /// Unity's cross-platform HTTP layer and works on ALL platforms including
  /// WebGL, iOS, Android, Windows, macOS, and consoles.
  /// </para>
  ///
  /// <para>
  /// The awaiter wrapper (<see cref="UnityWebRequestAwaiter"/>) uses
  /// <c>SendWebRequest()</c> which resumes on the Unity main thread — no
  /// explicit marshaling back to the main thread is required.
  /// </para>
  ///
  /// <para>
  /// JSON is serialized and deserialized via the core <see cref="Wire"/>
  /// helpers (<c>ToJson()</c>, <c>ActivateResponse.Parse()</c>, etc.) — the
  /// same code path used by the <c>HttpClientTransport</c>.
  /// </para>
  /// </summary>
  public sealed class UnityWebRequestTransport : IKeylightTransport {
    private readonly string _baseUrl;
    private readonly string _tenantId;
    private readonly string _productId;
    private readonly string _sdkKey;

    /// <summary>
    /// Creates a new <see cref="UnityWebRequestTransport"/>.
    /// </summary>
    /// <param name="baseUrl">
    ///   Root URL of the Keylight API (default: <c>https://api.keylight.dev</c>).
    /// </param>
    /// <param name="tenantId">Your Keylight tenant ID.</param>
    /// <param name="productId">Your Keylight product ID.</param>
    /// <param name="sdkKey">
    ///   Your Keylight SDK key; sent as <c>X-Keylight-SDK-Key</c>.
    /// </param>
    public UnityWebRequestTransport(
      string baseUrl,
      string tenantId,
      string productId,
      string sdkKey) {
      _baseUrl   = baseUrl.TrimEnd('/');
      _tenantId  = tenantId;
      _productId = productId;
      _sdkKey    = sdkKey;
    }

    // ─── IKeylightTransport ──────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ActivateResponse> ActivateAsync(
      ActivateRequest req,
      CancellationToken ct = default) {
      var body = await PostJsonAsync("activate", req.ToJson(), ct).ConfigureAwait(false);
      var result = ActivateResponse.Parse(body);
      if (result == null) throw new InvalidOperationException("Empty or null response from Keylight API.");
      return result;
    }

    /// <inheritdoc />
    public async Task<ValidateResponse> ValidateAsync(
      ValidateRequest req,
      CancellationToken ct = default) {
      var body = await PostJsonAsync("validate", req.ToJson(), ct).ConfigureAwait(false);
      var result = ValidateResponse.Parse(body);
      if (result == null) throw new InvalidOperationException("Empty or null response from Keylight API.");
      return result;
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(
      DeactivateRequest req,
      CancellationToken ct = default) {
      await PostJsonAsync("deactivate", req.ToJson(), ct).ConfigureAwait(false);
    }

    // ─── private helpers ─────────────────────────────────────────────────────

    private string BuildUrl(string action) =>
      $"{_baseUrl}/{_tenantId}/{_productId}/{action}";

    /// <summary>
    /// POST <paramref name="jsonBody"/> to <c>{baseUrl}/{tenantId}/{productId}/{action}</c>,
    /// wait for the response, and return the response body as a string.
    /// Throws <see cref="ActivationException"/> for non-2xx status codes (mirrors
    /// the behaviour of <c>HttpClientTransport</c>).
    /// </summary>
    private async Task<string> PostJsonAsync(
      string action,
      string jsonBody,
      CancellationToken ct) {

      var url     = BuildUrl(action);
      var payload = Encoding.UTF8.GetBytes(jsonBody);

      // UnityWebRequest is a Unity main-thread object; creation is cheap.
      using var uwr = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST) {
        uploadHandler   = new UploadHandlerRaw(payload) { contentType = "application/json" },
        downloadHandler = new DownloadHandlerBuffer()
      };
      uwr.SetRequestHeader("Content-Type",          "application/json");
      uwr.SetRequestHeader("X-Keylight-SDK-Key",    _sdkKey);

      // Await the web request.  UnityWebRequestAwaiter resumes on the main thread
      // (Unity's SendWebRequest callback), which is correct — UnityEngine objects
      // must only be accessed from the main thread.
      await uwr.SendWebRequest();

      // CancellationToken check: UnityWebRequest doesn't natively support
      // cancellation tokens, so we check after completion (best-effort).
      ct.ThrowIfCancellationRequested();

      // Non-2xx → ActivationException with the HTTP status code.
#if UNITY_2021_3_OR_NEWER
      var statusCode = (int)uwr.responseCode;
#else
      var statusCode = 0;
#endif
      if (uwr.result == UnityWebRequest.Result.ConnectionError ||
          uwr.result == UnityWebRequest.Result.DataProcessingError) {
        throw new ActivationException(0, $"Network error: {uwr.error}");
      }
      if (statusCode < 200 || statusCode >= 300) {
        throw new ActivationException(statusCode,
          $"Keylight API returned HTTP {statusCode}: {uwr.downloadHandler.text}");
      }

      return uwr.downloadHandler.text;
    }
  }

  // ─── UnityWebRequest awaiter ─────────────────────────────────────────────

  /// <summary>
  /// Minimal GetAwaiter / awaiter implementation for <see cref="UnityWebRequestAsyncOperation"/>
  /// so Unity web requests can be used with <c>await</c>.
  ///
  /// The continuation is registered via <c>AsyncOperation.completed</c>, which
  /// Unity always fires on the main thread — so awaiting a
  /// <see cref="UnityWebRequest"/> automatically resumes on the main thread.
  /// No <c>SynchronizationContext</c> marshaling is needed.
  /// </summary>
  internal sealed class UnityWebRequestAwaiter : INotifyCompletion {
    private readonly UnityWebRequestAsyncOperation _op;
    private Action? _continuation;

    internal UnityWebRequestAwaiter(UnityWebRequestAsyncOperation op) {
      _op = op;
    }

    public bool IsCompleted => _op.isDone;

    public void OnCompleted(Action continuation) {
      if (_op.isDone) {
        continuation();
        return;
      }
      _continuation = continuation;
      _op.completed += _ => _continuation?.Invoke();
    }

    public UnityWebRequest GetResult() => _op.webRequest;
  }

  /// <summary>
  /// Extension that allows <c>await uwr.SendWebRequest();</c> syntax.
  /// </summary>
  internal static class UnityWebRequestExtensions {
    internal static UnityWebRequestAwaiter GetAwaiter(
      this UnityWebRequestAsyncOperation op) =>
        new UnityWebRequestAwaiter(op);
  }
}
#endif // UNITY_2021_3_OR_NEWER
