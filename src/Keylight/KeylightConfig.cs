using System;
using System.Collections.Generic;

namespace Keylight {
  public sealed class KeylightConfig {
    public string TenantId { get; }
    public string ProductId { get; }
    public string SdkKey { get; }
    public IReadOnlyDictionary<string, string> TrustedKeys { get; }
    public int MaxOfflineDays { get; }
    public string? KeyPrefix { get; }
    public int? TrialDurationDays { get; }
    public string BaseUrl { get; }
    public string? AppVersion { get; }
    public string? Platform { get; }
    /// <summary>
    /// Require server-owned product settings to carry a valid Ed25519 signature
    /// before they are cached. Defaults to <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Off by default on purpose: the worker signs a product's settings only
    /// once that product has a trial length configured, and every other product
    /// is served unsigned. Turning this on for one of those would reject
    /// legitimate responses and pin the install to its compiled-in seed.
    ///
    /// Trust is rooted in <see cref="TrustedKeys"/>, which you compile into the
    /// app. The SDK deliberately does not fetch a keyset at runtime — keys
    /// fetched over the same channel that serves the config would let anyone
    /// able to forge one forge the other. <see cref="Keyset.FetchAsync"/> exists
    /// for bootstrapping and samples, not as a trust root. The cost is that
    /// rotating to a new kid leaves shipped builds on their last cached settings
    /// until they update: a freeze, not a failure.
    /// </remarks>
    public bool RequireSignedConfig { get; }

    private KeylightConfig(
      string tenantId,
      string productId,
      string sdkKey,
      IReadOnlyDictionary<string, string> trustedKeys,
      int maxOfflineDays,
      string? keyPrefix,
      int? trialDurationDays,
      string baseUrl,
      string? appVersion,
      string? platform,
      bool requireSignedConfig)
    {
      TenantId = tenantId;
      ProductId = productId;
      SdkKey = sdkKey;
      TrustedKeys = trustedKeys;
      MaxOfflineDays = maxOfflineDays;
      KeyPrefix = keyPrefix;
      TrialDurationDays = trialDurationDays;
      BaseUrl = baseUrl;
      AppVersion = appVersion;
      Platform = platform;
      RequireSignedConfig = requireSignedConfig;
    }

    public static ConfigBuilder Builder(string tenantId, string productId, string sdkKey)
      => new ConfigBuilder(tenantId, productId, sdkKey);

    public sealed class ConfigBuilder {
      private readonly string _tenantId;
      private readonly string _productId;
      private readonly string _sdkKey;
      private IReadOnlyDictionary<string, string> _trustedKeys = new Dictionary<string, string>();
      private int _maxOfflineDays = 15;
      private string? _keyPrefix;
      private int? _trialDurationDays;
      private string _baseUrl = "https://api.keylight.dev";
      private string? _appVersion;
      private string? _platform;
      private bool _requireSignedConfig;

      internal ConfigBuilder(string tenantId, string productId, string sdkKey) {
        _tenantId = tenantId;
        _productId = productId;
        _sdkKey = sdkKey;
      }

      public ConfigBuilder TrustedKeys(IDictionary<string, string> keys) {
        _trustedKeys = new Dictionary<string, string>(keys);
        return this;
      }

      /// <summary>
      /// Reject server-owned settings that do not carry a valid signature. See
      /// <see cref="KeylightConfig.RequireSignedConfig"/> before enabling it —
      /// the worker only signs products that have a trial length configured.
      /// </summary>
      public ConfigBuilder RequireSignedConfig(bool require = true) {
        _requireSignedConfig = require;
        return this;
      }

      public ConfigBuilder MaxOfflineDays(int days) {
        _maxOfflineDays = days;
        return this;
      }

      public ConfigBuilder KeyPrefix(string prefix) {
        _keyPrefix = prefix;
        return this;
      }

      public ConfigBuilder TrialDurationDays(int days) {
        _trialDurationDays = days;
        return this;
      }

      public ConfigBuilder BaseUrl(string url) {
        _baseUrl = url;
        return this;
      }

      public ConfigBuilder AppVersion(string version) {
        _appVersion = version;
        return this;
      }

      public ConfigBuilder Platform(string platform) {
        _platform = platform;
        return this;
      }

      public KeylightConfig Build() {
        if (string.IsNullOrEmpty(_tenantId))
          throw new ArgumentException("tenantId must not be empty.", nameof(_tenantId));
        if (string.IsNullOrEmpty(_productId))
          throw new ArgumentException("productId must not be empty.", nameof(_productId));
        if (string.IsNullOrEmpty(_sdkKey))
          throw new ArgumentException("sdkKey must not be empty.", nameof(_sdkKey));

        return new KeylightConfig(
          tenantId: _tenantId,
          productId: _productId,
          sdkKey: _sdkKey,
          trustedKeys: _trustedKeys,
          maxOfflineDays: _maxOfflineDays,
          keyPrefix: _keyPrefix,
          trialDurationDays: _trialDurationDays,
          baseUrl: _baseUrl,
          requireSignedConfig: _requireSignedConfig,
          appVersion: _appVersion,
          platform: _platform
        );
      }
    }
  }
}
