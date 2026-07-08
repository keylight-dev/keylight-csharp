using System;
using System.Collections.Generic;
using Xunit;
using Keylight;

namespace Keylight.Tests {
  public class ConfigTests {
    [Fact]
    public void Builder_RequiredFields_AppliesDefaults() {
      var cfg = KeylightConfig.Builder("tenant-1", "product-1", "sk-test")
        .Build();

      Assert.Equal("tenant-1", cfg.TenantId);
      Assert.Equal("product-1", cfg.ProductId);
      Assert.Equal("sk-test", cfg.SdkKey);
      Assert.Equal(15, cfg.MaxOfflineDays);
      Assert.Equal("https://api.keylight.dev", cfg.BaseUrl);
      Assert.NotNull(cfg.TrustedKeys);
      Assert.Empty(cfg.TrustedKeys);
      Assert.Null(cfg.KeyPrefix);
      Assert.Null(cfg.TrialDurationDays);
      Assert.Null(cfg.AppVersion);
    }

    [Fact]
    public void Builder_AllFields_RoundTrip() {
      var keys = new Dictionary<string, string> { ["kid1"] = "abc123" };
      var cfg = KeylightConfig.Builder("t", "p", "sk-x")
        .TrustedKeys(keys)
        .MaxOfflineDays(14)
        .KeyPrefix("KL-")
        .TrialDurationDays(30)
        .BaseUrl("https://custom.example.com")
        .AppVersion("2.0.0")
        .Build();

      Assert.Equal(14, cfg.MaxOfflineDays);
      Assert.Equal("KL-", cfg.KeyPrefix);
      Assert.Equal(30, cfg.TrialDurationDays);
      Assert.Equal("https://custom.example.com", cfg.BaseUrl);
      Assert.Equal("2.0.0", cfg.AppVersion);
      Assert.Single(cfg.TrustedKeys);
      Assert.Equal("abc123", cfg.TrustedKeys["kid1"]);
    }

    [Theory]
    [InlineData("", "product-1", "sk-test")]
    [InlineData("tenant-1", "", "sk-test")]
    [InlineData("tenant-1", "product-1", "")]
    public void Builder_EmptyRequiredField_ThrowsArgumentException(string tenantId, string productId, string sdkKey) {
      Assert.Throws<ArgumentException>(() =>
        KeylightConfig.Builder(tenantId, productId, sdkKey).Build());
    }
  }
}
