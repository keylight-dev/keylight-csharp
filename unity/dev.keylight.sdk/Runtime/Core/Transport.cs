using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Keylight {
  /// <summary>Abstraction over the Keylight HTTP API for testability.</summary>
  public interface IKeylightTransport {
    Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default);
    Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default);
    Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default);

  }

  /// <summary>
  /// Optional transport capability: <c>GET {baseUrl}/{tenant}/{product}/config</c>,
  /// the server-owned trial length and free-tier flag.
  /// </summary>
  /// <remarks>
  /// A separate interface rather than a member on <see cref="IKeylightTransport"/>
  /// so the addition is non-breaking: existing custom transports and test doubles
  /// keep compiling untouched. A default interface method would have been the
  /// tidier shape, but this assembly also targets netstandard2.0 (Unity), whose
  /// runtime cannot dispatch one. The client probes with a type test and simply
  /// skips the fetch when the transport does not implement this — leaving the
  /// cached settings in place rather than falling back to the seed.
  /// </remarks>
  public interface IKeylightConfigTransport {
    Task<ConfigResponse?> FetchConfigAsync(CancellationToken ct = default);
  }

  /// <summary>
  /// Optional transport capability: <c>POST {baseUrl}/{tenant}/{product}/keyless</c>.
  /// Separate from <see cref="IKeylightTransport"/> for the same reason as
  /// <see cref="IKeylightConfigTransport"/>: existing custom transports keep
  /// compiling, and netstandard2.0 cannot dispatch a default interface method.
  /// A transport without it simply never beacons.
  /// </summary>
  public interface IKeylightKeylessTransport {
    Task<KeylessResponse?> ReportKeylessAsync(KeylessRequest req, CancellationToken ct = default);
  }

  /// <summary>
  /// Default transport that POSTs JSON to the Keylight API using <see cref="HttpClient"/>.
  /// URL pattern: <c>{baseUrl}/{tenantId}/{productId}/{action}</c>.
  /// </summary>
  public sealed class HttpClientTransport : IKeylightTransport, IKeylightConfigTransport, IKeylightKeylessTransport {
    private readonly string _baseUrl;
    private readonly string _tenantId;
    private readonly string _productId;
    private readonly string _sdkKey;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public HttpClientTransport(
      string baseUrl,
      string tenantId,
      string productId,
      string sdkKey,
      HttpClient? httpClient = null) {
      _baseUrl = baseUrl.TrimEnd('/');
      _tenantId = tenantId;
      _productId = productId;
      _sdkKey = sdkKey;
      _ownsClient = httpClient == null;
      _http = httpClient ?? new HttpClient();
    }

    private string BuildUrl(string action) =>
      TransportHelpers.BuildUrl(_baseUrl, _tenantId, _productId, action);

    private HttpRequestMessage BuildRequest(string action, string jsonBody) {
      var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
      var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(action)) {
        Content = content
      };
      request.Headers.Add("X-Keylight-SDK-Key", _sdkKey);
      return request;
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct) {
#if NET5_0_OR_GREATER
      var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
#else
      // .NET Standard 2.0/2.1 and Unity's BCL have no CancellationToken overload.
      var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#endif
      TransportHelpers.EnsureSuccess((int)response.StatusCode, body);
      return body;
    }

    public async Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default) {
      using var request = BuildRequest("activate", req.ToJson());
      var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
      var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);
      var result = ActivateResponse.Parse(body);
      if (result == null) throw new InvalidOperationException("Empty or null response from Keylight API.");
      return result;
    }

    public async Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default) {
      using var request = BuildRequest("validate", req.ToJson());
      var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
      var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);
      var result = ValidateResponse.Parse(body);
      if (result == null) throw new InvalidOperationException("Empty or null response from Keylight API.");
      return result;
    }

    public async Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default) {
      using var request = BuildRequest("deactivate", req.ToJson());
      var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
      await ReadBodyAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<ConfigResponse?> FetchConfigAsync(CancellationToken ct = default) {
      using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl("config"));
      request.Headers.Add("X-Keylight-SDK-Key", _sdkKey);
      var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
      var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);
      return ConfigResponse.Parse(body);
    }

    public async Task<KeylessResponse?> ReportKeylessAsync(KeylessRequest req, CancellationToken ct = default) {
      using var request = BuildRequest("keyless", req.ToJson());
      var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
      var body = await ReadBodyAsync(response, ct).ConfigureAwait(false); // throws on non-2xx
      return KeylessResponse.Parse(body);
    }

    public void Dispose() {
      if (_ownsClient) _http.Dispose();
    }
  }

  /// <summary>Shared transport helpers used by both HttpClientTransport and UnityWebRequestTransport.</summary>
  internal static class TransportHelpers {
    internal static string BuildUrl(string baseUrl, string tenantId, string productId, string action) =>
      $"{baseUrl}/{tenantId}/{productId}/{action}";

    internal static void EnsureSuccess(int statusCode, string body) {
      if (statusCode < 200 || statusCode >= 300)
        throw new ActivationException(statusCode,
          $"Keylight API returned HTTP {statusCode}: {body}", body);
    }
  }
}
