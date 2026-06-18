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
  /// Default transport that POSTs JSON to the Keylight API using <see cref="HttpClient"/>.
  /// URL pattern: <c>{baseUrl}/{tenantId}/{productId}/{action}</c>.
  /// </summary>
  public sealed class HttpClientTransport : IKeylightTransport {
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
#if NETSTANDARD2_0
      var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#else
      var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
          $"Keylight API returned HTTP {statusCode}: {body}");
    }
  }
}
