using Keylight;
using Xunit;

namespace Keylight.Tests {

  /// <summary>
  /// The CPU-core and memory buckets are a CROSS-SDK CONTRACT: Swift, Rust, JS,
  /// C++ and this SDK must all produce byte-identical strings and must all place
  /// a boundary value in the same bucket. A boundary that disagrees with another
  /// SDK splits one machine population across two rows in the dashboard — the
  /// exact bug that the macOS `os_version` normalization had to fix.
  ///
  /// The contract:
  ///   cpu_cores : "1-2" | "3-4" | "5-8" | "9-16" | "17+"
  ///               ranges are INCLUSIVE of both endpoints (4 -> "3-4", 5 -> "5-8")
  ///   memory    : "&lt;4GB" | "4-8GB" | "8-16GB" | "16-32GB" | "32-64GB" | "64GB+"
  ///               the UPPER bound is EXCLUSIVE (exactly 8GiB -> "8-16GB")
  ///
  /// Memory is compared against the raw byte count with GiB = 1024^3, never a
  /// pre-rounded gigabyte figure: physical RAM as the OS reports it rarely lands
  /// exactly on a power of two, and rounding first moves machines across the
  /// boundary.
  /// </summary>
  public class DeviceBucketsTests {

    private const long GiB = 1024L * 1024L * 1024L;

    // ---- CPU cores -------------------------------------------------------

    [Theory]
    [InlineData(1,  "1-2")]
    [InlineData(2,  "1-2")]
    [InlineData(3,  "3-4")]
    [InlineData(4,  "3-4")]   // upper endpoint is INCLUSIVE
    [InlineData(5,  "5-8")]   // ...so 5 starts the next bucket
    [InlineData(8,  "5-8")]
    [InlineData(9,  "9-16")]
    [InlineData(16, "9-16")]
    [InlineData(17, "17+")]
    public void BucketCpuCores_matches_the_cross_sdk_boundaries(int cores, string expected) {
      Assert.Equal(expected, DeviceBuckets.BucketCpuCores(cores));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BucketCpuCores_returns_null_for_a_nonsensical_count(int cores) {
      // Better to send nothing than to invent a bucket. The field is optional.
      Assert.Null(DeviceBuckets.BucketCpuCores(cores));
    }

    [Fact]
    public void BucketCpuCores_handles_an_absurdly_large_count() {
      Assert.Equal("17+", DeviceBuckets.BucketCpuCores(int.MaxValue));
    }

    // ---- Memory ----------------------------------------------------------

    [Theory]
    [InlineData(3.9,   "<4GB")]
    [InlineData(4.0,   "4-8GB")]    // exactly 4GiB is IN "4-8GB"
    [InlineData(7.9,   "4-8GB")]
    [InlineData(8.0,   "8-16GB")]   // exactly 8GiB is IN "8-16GB", not "4-8GB"
    [InlineData(16.0,  "16-32GB")]
    [InlineData(32.0,  "32-64GB")]
    [InlineData(64.0,  "64GB+")]
    [InlineData(128.0, "64GB+")]
    public void BucketMemoryBytes_matches_the_cross_sdk_boundaries(double gib, string expected) {
      Assert.Equal(expected, DeviceBuckets.BucketMemoryBytes((long)(gib * GiB)));
    }

    [Theory]
    [InlineData(0.5,  "<4GB")]
    [InlineData(15.99, "8-16GB")]
    [InlineData(31.99, "16-32GB")]
    [InlineData(63.99, "32-64GB")]
    public void BucketMemoryBytes_keeps_just_under_a_boundary_in_the_lower_bucket(
        double gib, string expected) {
      Assert.Equal(expected, DeviceBuckets.BucketMemoryBytes((long)(gib * GiB)));
    }

    [Fact]
    public void BucketMemoryBytes_uses_GiB_not_decimal_GB() {
      // 4_000_000_000 bytes is "4 GB" decimal but only 3.73 GiB. Bucketing on
      // decimal gigabytes would file it under "4-8GB" and disagree with every
      // other SDK, which all compute in 1024^3.
      Assert.Equal("<4GB", DeviceBuckets.BucketMemoryBytes(4_000_000_000L));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void BucketMemoryBytes_returns_null_when_the_probe_failed(long bytes) {
      Assert.Null(DeviceBuckets.BucketMemoryBytes(bytes));
    }

    // ---- Vocabulary ------------------------------------------------------

    [Fact]
    public void Buckets_only_ever_emit_allow_listed_strings() {
      // The worker drops anything off its allow-list, so a typo here is a
      // silently-empty dashboard column rather than an error.
      var cpu    = new[] { "1-2", "3-4", "5-8", "9-16", "17+" };
      var memory = new[] { "<4GB", "4-8GB", "8-16GB", "16-32GB", "32-64GB", "64GB+" };

      for (int n = 1; n <= 64; n++)
        Assert.Contains(DeviceBuckets.BucketCpuCores(n), cpu);

      for (int g = 1; g <= 256; g++)
        Assert.Contains(DeviceBuckets.BucketMemoryBytes((long)g * GiB), memory);
    }

    [Fact]
    public void Buckets_fit_the_api_field_cap() {
      // Both fields are z.string().max(16) on the worker; over-long values
      // reject the WHOLE request body with a 400, they are not dropped.
      for (int n = 1; n <= 64; n++)
        Assert.True(DeviceBuckets.BucketCpuCores(n)!.Length <= 16);
      for (int g = 1; g <= 256; g++)
        Assert.True(DeviceBuckets.BucketMemoryBytes((long)g * GiB)!.Length <= 16);
    }
  }
}
