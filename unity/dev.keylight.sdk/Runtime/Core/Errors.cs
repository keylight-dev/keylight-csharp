using System;

namespace Keylight {
  /// <summary>
  /// Base class for all Keylight SDK exceptions.
  /// </summary>
  public class KeylightException : Exception {
    public KeylightException(string message) : base(message) { }
    public KeylightException(string message, Exception inner) : base(message, inner) { }
  }

  /// <summary>
  /// Thrown when a lease received from the server fails Ed25519 signature
  /// verification. The lease was not signed by a trusted key.
  /// </summary>
  public sealed class LeaseVerificationFailedException : KeylightException {
    public LeaseVerificationFailedException()
      : base("Lease signature verification failed: lease is not trusted.") { }
  }

  /// <summary>
  /// Thrown when the /activate endpoint returns a non-success HTTP status or
  /// an <c>activated=false</c> payload. Carries the HTTP status code for
  /// programmatic handling.
  /// </summary>
  public sealed class ActivationException : KeylightException {
    /// <summary>HTTP status code from the server (e.g. 409, 422, 429, 500).</summary>
    public int StatusCode { get; }

    /// <summary>
    /// Raw HTTP response body, when one was received (i.e. the server actually
    /// responded — as opposed to a transport-level failure with no response at
    /// all). Callers that need to distinguish a decodable definitive-rejection
    /// body (e.g. <c>/validate</c>'s HTTP 422 revoke shape) from a genuine
    /// network error can key off this being non-null.
    /// </summary>
    public string? Body { get; }

    public ActivationException(int statusCode, string message, string? body = null)
      : base(message) {
      StatusCode = statusCode;
      Body = body;
    }

    /// <summary>Constructs from a server error response with no extra message.</summary>
    public ActivationException(int statusCode)
      : this(statusCode, $"Activation failed (HTTP {statusCode})") { }
  }
}
