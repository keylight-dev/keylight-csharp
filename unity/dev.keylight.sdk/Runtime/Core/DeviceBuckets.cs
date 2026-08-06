namespace Keylight {
  /// <summary>
  /// Pure bucketing for the device-capability telemetry fields
  /// (<c>cpu_cores</c>, <c>memory</c>).
  ///
  /// <para><b>Why buckets and not raw values.</b> A developer proxying their own
  /// app's traffic must never see a licensing SDK reporting the machine's exact
  /// RAM size or core count — that reads as fingerprinting. Only the bucket
  /// crosses the wire; the precise value never leaves the process. This is a
  /// product decision, not an implementation shortcut.</para>
  ///
  /// <para><b>Cross-SDK contract.</b> Swift, Rust, JS, C++ and this SDK must all
  /// emit byte-identical strings AND agree on every boundary. A boundary that
  /// disagrees splits one machine population across two dashboard rows — the bug
  /// that had to be fixed for the macOS <c>os_version</c> token. The worker
  /// allow-lists these exact strings (<c>worker/src/telemetry/platform.ts</c>)
  /// and silently drops anything else.</para>
  ///
  /// <list type="bullet">
  ///   <item>cpu_cores: <c>1-2</c> | <c>3-4</c> | <c>5-8</c> | <c>9-16</c> | <c>17+</c>
  ///         — both endpoints INCLUSIVE (4 -> <c>3-4</c>, 5 -> <c>5-8</c>).</item>
  ///   <item>memory: <c>&lt;4GB</c> | <c>4-8GB</c> | <c>8-16GB</c> | <c>16-32GB</c> |
  ///         <c>32-64GB</c> | <c>64GB+</c> — upper bound EXCLUSIVE, so exactly
  ///         8GiB is <c>8-16GB</c>.</item>
  /// </list>
  ///
  /// Memory is bucketed from the RAW BYTE COUNT with GiB = 1024^3. Physical RAM
  /// as the OS reports it rarely lands exactly on a power of two, so rounding to
  /// whole gigabytes first would move machines across a boundary and disagree
  /// with the other SDKs.
  ///
  /// Kept as a separate pure type (no probing, no I/O) so the boundaries can be
  /// tested exhaustively without a machine that has 64GB of RAM.
  /// </summary>
  public static class DeviceBuckets {
    private const long GiB = 1024L * 1024L * 1024L;

    /// <summary>
    /// Buckets a CPU-core count. Returns <c>null</c> for a nonsensical count
    /// (&lt;= 0) — the field is optional, and omitting beats inventing.
    /// </summary>
    public static string? BucketCpuCores(int cores) {
      if (cores <= 0) return null;
      if (cores <= 2) return "1-2";
      if (cores <= 4) return "3-4";
      if (cores <= 8) return "5-8";
      if (cores <= 16) return "9-16";
      return "17+";
    }

    /// <summary>
    /// Buckets a physical-RAM size given in BYTES. Returns <c>null</c> when the
    /// probe failed (<paramref name="bytes"/> &lt;= 0) so the field is omitted
    /// rather than reported as "&lt;4GB", which would misfile the device.
    /// </summary>
    public static string? BucketMemoryBytes(long bytes) {
      if (bytes <= 0) return null;
      if (bytes < 4L * GiB) return "<4GB";
      if (bytes < 8L * GiB) return "4-8GB";
      if (bytes < 16L * GiB) return "8-16GB";
      if (bytes < 32L * GiB) return "16-32GB";
      if (bytes < 64L * GiB) return "32-64GB";
      return "64GB+";
    }
  }
}
