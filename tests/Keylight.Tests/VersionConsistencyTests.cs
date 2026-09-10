using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Keylight.Tests {
  /// <summary>
  /// The three places this SDK states its own version must agree.
  ///
  /// They have drifted twice. `unity/dev.keylight.sdk/package.json` sat at 0.1.3
  /// while the NuGet package went to 0.2.0, because `unity/sync-core.sh` copies
  /// only `.cs` files and nothing bumped the manifest. That is invisible in
  /// normal use — until OpenUPM, which refuses to build a tag whose
  /// package.json version does not match it, and until a Unity bug report
  /// arrives quoting a version that was never released.
  ///
  /// This is a test rather than a release-checklist line because a checklist is
  /// what failed.
  /// </summary>
  public class VersionConsistencyTests {
    static string RepoRoot() {
      var dir = AppContext.BaseDirectory;
      while (dir != null && !File.Exists(Path.Combine(dir, "Keylight.slnx"))) {
        dir = Path.GetDirectoryName(dir);
      }
      Assert.NotNull(dir);
      return dir!;
    }

    static string CsprojVersion() {
      var path = Path.Combine(RepoRoot(), "src", "Keylight", "Keylight.csproj");
      var m = Regex.Match(File.ReadAllText(path), @"<Version>([^<]+)</Version>");
      Assert.True(m.Success, $"no <Version> in {path}");
      return m.Groups[1].Value.Trim();
    }

    static string UnityPackageVersion() {
      var path = Path.Combine(RepoRoot(), "unity", "dev.keylight.sdk", "package.json");
      using var doc = JsonDocument.Parse(File.ReadAllText(path));
      return doc.RootElement.GetProperty("version").GetString()!;
    }

    [Fact]
    public void SdkInfoVersion_MatchesCsproj() {
      // SdkInfo.Version is what every activate/validate call reports as
      // sdk_version, so a mismatch silently mislabels the whole telemetry
      // funnel for this SDK.
      Assert.Equal(CsprojVersion(), SdkVersionUnderTest());
    }

    [Fact]
    public void UnityPackage_MatchesCsproj() {
      // OpenUPM builds a tag only when package.json agrees with it, and the tag
      // follows the csproj version.
      Assert.Equal(CsprojVersion(), UnityPackageVersion());
    }

    /// <summary>
    /// SdkInfo is internal, so read it the same way the build does rather than
    /// widening its visibility for a test.
    /// </summary>
    static string SdkVersionUnderTest() {
      var path = Path.Combine(RepoRoot(), "src", "Keylight", "Version.cs");
      var m = Regex.Match(File.ReadAllText(path), @"Version\s*=\s*""([^""]+)""");
      Assert.True(m.Success, $"no version constant in {path}");
      return m.Groups[1].Value;
    }

    [Fact]
    public void UnityMirror_IsInSyncWithSource() {
      // unity/dev.keylight.sdk/Runtime/Core/ is generated from src/Keylight/ by
      // unity/sync-core.sh. When someone edits src/ and forgets to regenerate,
      // Unity users compile against an older client — which is exactly how the
      // 0.2.0 trial work reached NuGet but never reached Unity.
      var src = Path.Combine(RepoRoot(), "src", "Keylight");
      var mirror = Path.Combine(RepoRoot(), "unity", "dev.keylight.sdk", "Runtime", "Core");

      foreach (var file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)) {
        if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
        if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

        var relative = Path.GetRelativePath(src, file);
        var mirrored = Path.Combine(mirror, relative);
        Assert.True(File.Exists(mirrored), $"{relative} is missing from the Unity mirror — run ./unity/sync-core.sh");
        Assert.True(
          File.ReadAllText(file) == File.ReadAllText(mirrored),
          $"{relative} differs from the Unity mirror — run ./unity/sync-core.sh");
      }
    }
  }
}
