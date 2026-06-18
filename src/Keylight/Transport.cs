using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    // Exposed for tests so they serialize with the same options.
    public static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions {
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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
      $"{_baseUrl}/{_tenantId}/{_productId}/{action}";

    private HttpRequestMessage BuildRequest<T>(string action, T body) {
      var json = JsonSerializer.Serialize(body, SerializerOptions);
      var content = new StringContent(json, Encoding.UTF8, "application/json");
      var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(action)) {
        Content = content
      };
      request.Headers.Add("X-Keylight-SDK-Key", _sdkKey);
      return request;
    }

    private static async Task<TResponse> ReadResponse<TResponse>(HttpResponseMessage response, CancellationToken ct) {
      response.EnsureSuccessStatusCode();
#if NETSTANDARD2_0
      var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#else
      var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
#endif
      var result = JsonSerializer.Deserialize<TResponse>(body, SerializerOptions);
      if (result == null) throw new InvalidOperationException("Empty or null response from Keylight API.");
      return result;
    }

    public async Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default) {
      using var request = BuildRequest("activate", req);
      var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
      return await ReadResponse<ActivateResponse>(response, ct).ConfigureAwait(false);
    }

    public async Task<ValidateResponse> ValidateAsync(ValidateRequest req, CancellationToken ct = default) {
      using var request = BuildRequest("validate", req);
      var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
      return await ReadResponse<ValidateResponse>(response, ct).ConfigureAwait(false);
    }

    public async Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default) {
      using var request = BuildRequest("deactivate", req);
      var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
    }

    public void Dispose() {
      if (_ownsClient) _http.Dispose();
    }
  }
}
