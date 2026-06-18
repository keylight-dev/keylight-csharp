using System.Text.Json.Serialization;

namespace Keylight {
  /// <summary>Request body for the /activate endpoint.</summary>
  public sealed class ActivateRequest {
    [JsonPropertyName("license_key")] public string LicenseKey { get; set; } = "";
    [JsonPropertyName("instance_name")] public string InstanceName { get; set; } = "";
    [JsonPropertyName("app_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AppVersion { get; set; }
    [JsonPropertyName("sdk_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SdkVersion { get; set; }
    [JsonPropertyName("platform")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Platform { get; set; }
    [JsonPropertyName("free_tier_instance_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FreeTierInstanceId { get; set; }
  }

  /// <summary>Request body for the /validate endpoint.</summary>
  public sealed class ValidateRequest {
    [JsonPropertyName("instance_id")] public string InstanceId { get; set; } = "";
    [JsonPropertyName("app_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AppVersion { get; set; }
    [JsonPropertyName("sdk_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SdkVersion { get; set; }
    [JsonPropertyName("platform")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Platform { get; set; }
  }

  /// <summary>Request body for the /deactivate endpoint.</summary>
  public sealed class DeactivateRequest {
    [JsonPropertyName("instance_id")] public string InstanceId { get; set; } = "";
  }

  /// <summary>Response body from the /activate endpoint.</summary>
  public sealed class ActivateResponse {
    [JsonPropertyName("activated")] public bool Activated { get; set; }
    [JsonPropertyName("instance_id")] public string? InstanceId { get; set; }
    [JsonPropertyName("license_expires_at")] public long? LicenseExpiresAt { get; set; }
    [JsonPropertyName("lease")] public Lease? Lease { get; set; }
  }

  /// <summary>Response body from the /validate endpoint.</summary>
  public sealed class ValidateResponse {
    [JsonPropertyName("valid")] public bool Valid { get; set; }
    [JsonPropertyName("license_expires_at")] public long? LicenseExpiresAt { get; set; }
    [JsonPropertyName("lease")] public Lease? Lease { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
  }
}
