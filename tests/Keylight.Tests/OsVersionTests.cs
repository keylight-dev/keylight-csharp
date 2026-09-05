using System.Runtime.InteropServices;
using Keylight;
using Xunit;

namespace Keylight.Tests;

/// <summary>
/// This SDK was the only one of five sending neither os_version nor arch, so
/// C# apps contributed nothing to two breakdowns everyone else populates.
/// </summary>
public class OsVersionTests {
  [Theory]
  [InlineData("15.5", "15.5")]
  [InlineData("6.8.0-45-generic", "6.8.0")]   // Linux kernel release
  [InlineData("10.0.22631.3737", "10.0.22631.3737")]
  [InlineData(" 14.5 ", "14.5")]
  [InlineData("14.", "14")]                    // trailing dot dropped, not sent
  public void KeepsTheLeadingDottedNumericRun(string raw, string expected) {
    Assert.Equal(expected, OsVersion.DottedNumeric(raw));
  }

  [Theory]
  [InlineData("Sonoma")]
  [InlineData("v14.5")]        // must not silently become 14.5 — it did not start numeric
  [InlineData("1..2")]
  [InlineData(".5")]
  [InlineData("")]
  [InlineData(null)]
  public void OmitsAnythingItCannotReduceCleanly(string? raw) {
    Assert.Null(OsVersion.DottedNumeric(raw));
  }

  [Fact]
  public void RejectsAnOverLongRunRatherThanTruncating() {
    // Truncating would mint a fake version bucket out of a client bug.
    var tooLong = string.Join(".", System.Linq.Enumerable.Repeat("100", 20));
    Assert.True(tooLong.Length > 32);
    Assert.Null(OsVersion.DottedNumeric(tooLong));
  }

  [Fact]
  public void ArchIsCanonicalOrAbsent() {
    var arch = Device.Arch;
    if (arch != null) Assert.Contains(arch, new[] { "arm64", "x86_64" });
  }

  [Fact]
  public void OnThisMachineTheVersionIsDottedNumericOrAbsent() {
    var v = Device.OsVersionValue;
    if (v == null) return;
    Assert.Matches(@"^\d+(\.\d+)*$", v);
    Assert.True(v.Length <= 32);
  }

  [Fact]
  public void BothRideTheActivateAndValidatePayloads() {
    var a = new ActivateRequest { LicenseKey = "K", InstanceName = "n", OsVersion = "15.5", Arch = "arm64" };
    var json = a.GetType().GetMethod("ToJson", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
      .Invoke(a, null) as string;
    Assert.Contains("\"os_version\"", json);
    Assert.Contains("\"arch\"", json);

    var v = new ValidateRequest { OsVersion = "15.5", Arch = "arm64" };
    var vjson = v.GetType().GetMethod("ToJson", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
      .Invoke(v, null) as string;
    Assert.Contains("\"os_version\"", vjson);
    Assert.Contains("\"arch\"", vjson);
  }
}
