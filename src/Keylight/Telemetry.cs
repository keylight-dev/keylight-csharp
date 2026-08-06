namespace Keylight {
  /// <summary>
  /// Server-side caps on the telemetry fields sent with activate/validate.
  ///
  /// The API validates these with zod (<c>z.string().max(n)</c>) and rejects the
  /// WHOLE request body with a 400 when one is over-long — the field is not
  /// dropped and the request does not partially succeed. <c>AppVersion</c> comes
  /// from the host app and is entirely outside the SDK's control, so it must be
  /// clamped before it goes on the wire. This is the same class of bug as the
  /// <c>OSDescription</c> platform regression, which 400'd every activate and
  /// validate on macOS and Linux.
  ///
  /// Clamping lives here (and is applied in <see cref="ActivateRequest.ToJson"/>
  /// / <see cref="ValidateRequest.ToJson"/>) rather than at the call sites so
  /// there is no way to construct a request that bypasses it.
  ///
  /// Parity note: zod's <c>.max()</c> counts UTF-16 code units, and so does
  /// .NET's <c>string.Length</c>, so this comparison is exact — no surrogate
  /// arithmetic is needed to agree with the server.
  /// </summary>
  internal static class Telemetry {
    internal const int VersionMax = 64;
    internal const int PlatformMax = 32;
    internal const int SdkIdMax = 16;
    /// <summary>Cap on the device-capability buckets (<c>cpu_cores</c>,
    /// <c>memory</c>) — <c>z.string().max(16)</c> on the worker.</summary>
    internal const int BucketMax = 16;

    /// <summary>
    /// Identifies this SDK to the backend, sent as <c>sdk</c>.
    /// </summary>
    /// <remarks>
    /// <c>Platform</c> cannot identify the SDK. Once this SDK started sending
    /// canonical <c>macos</c>/<c>windows</c>/<c>linux</c> tokens it became
    /// indistinguishable from the Rust and C++ SDKs, and the server labelled
    /// every one of those devices "Rust". This field is the explicit answer.
    /// </remarks>
    internal const string SdkId = "csharp";

    /// <summary>
    /// Truncates to at most <paramref name="max"/> UTF-16 code units, never
    /// leaving a dangling high surrogate. A lone surrogate is not valid Unicode
    /// and would serialize as U+FFFD, so a value cut mid-pair is dropped back to
    /// the last whole character.
    /// </summary>
    internal static string? Clamp(string? value, int max) {
      if (value == null || value.Length <= max) return value;
      var cut = max;
      if (char.IsHighSurrogate(value[cut - 1])) cut--;
      return value.Substring(0, cut);
    }
  }
}
