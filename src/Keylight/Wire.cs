using System;
using System.Collections.Generic;
using System.Globalization;
using Keylight.Json;

namespace Keylight {
  /// <summary>Request body for the /activate endpoint.</summary>
  public sealed class ActivateRequest {
    public string LicenseKey { get; set; } = "";
    public string InstanceName { get; set; } = "";
    public string? AppVersion { get; set; }
    public string? SdkVersion { get; set; }
    public string? Platform { get; set; }
    public string? FreeTierInstanceId { get; set; }

    internal string ToJson() {
      var obj = new Dictionary<string, object?> {
        ["license_key"]   = LicenseKey,
        ["instance_name"] = InstanceName,
        ["app_version"]   = AppVersion,
        ["sdk_version"]   = SdkVersion,
        ["platform"]      = Platform,
        ["free_tier_instance_id"] = FreeTierInstanceId
      };
      return JsonCodec.Stringify(obj);
    }
  }

  /// <summary>Request body for the /validate endpoint.</summary>
  public sealed class ValidateRequest {
    public string InstanceId { get; set; } = "";
    public string? AppVersion { get; set; }
    public string? SdkVersion { get; set; }
    public string? Platform { get; set; }

    internal string ToJson() {
      var obj = new Dictionary<string, object?> {
        ["instance_id"] = InstanceId,
        ["app_version"] = AppVersion,
        ["sdk_version"] = SdkVersion,
        ["platform"]    = Platform
      };
      return JsonCodec.Stringify(obj);
    }
  }

  /// <summary>Request body for the /deactivate endpoint.</summary>
  public sealed class DeactivateRequest {
    public string InstanceId { get; set; } = "";

    internal string ToJson() {
      var obj = new Dictionary<string, object?> {
        ["instance_id"] = InstanceId
      };
      return JsonCodec.Stringify(obj);
    }
  }

  /// <summary>Response body from the /activate endpoint.</summary>
  public sealed class ActivateResponse {
    public bool Activated { get; set; }
    public string? InstanceId { get; set; }
    public long? LicenseExpiresAt { get; set; }
    public Lease? Lease { get; set; }

    internal static ActivateResponse? Parse(string json) {
      var root = JsonCodec.Parse(json);
      if (root == null) return null;
      var resp = new ActivateResponse();
      resp.Activated = root.Get("activated")?.AsBool() ?? false;
      resp.InstanceId = root.Get("instance_id")?.AsString();
      var expAt = root.Get("license_expires_at");
      resp.LicenseExpiresAt = (expAt != null && !expAt.IsNull) ? expAt.AsLong() : null;
      var leaseNode = root.Get("lease");
      resp.Lease = (leaseNode != null && !leaseNode.IsNull) ? WireHelpers.ParseLease(leaseNode) : null;
      return resp;
    }
  }

  /// <summary>Response body from the /validate endpoint.</summary>
  public sealed class ValidateResponse {
    public bool Valid { get; set; }
    public long? LicenseExpiresAt { get; set; }
    public Lease? Lease { get; set; }
    public string? Error { get; set; }

    internal static ValidateResponse? Parse(string json) {
      var root = JsonCodec.Parse(json);
      if (root == null) return null;
      var resp = new ValidateResponse();
      resp.Valid = root.Get("valid")?.AsBool() ?? false;
      var expAt = root.Get("license_expires_at");
      resp.LicenseExpiresAt = (expAt != null && !expAt.IsNull) ? expAt.AsLong() : null;
      var leaseNode = root.Get("lease");
      resp.Lease = (leaseNode != null && !leaseNode.IsNull) ? WireHelpers.ParseLease(leaseNode) : null;
      resp.Error = root.Get("error")?.AsString();
      return resp;
    }
  }

  internal static class WireHelpers {
    /// <summary>
    /// Parse a Lease from a JsonValue node.
    /// Reads camelCase keys as they come from the server.
    /// </summary>
    internal static Lease? ParseLease(JsonValue node) {
      if (node == null || node.IsNull) return null;
      var lease = new Lease();
      lease.Kid            = node.Get("kid")?.AsString()            ?? "";
      lease.LicenseKeyHash = node.Get("licenseKeyHash")?.AsString() ?? "";
      lease.InstanceId     = node.Get("instanceId")?.AsString()     ?? "";
      lease.IssuedAt       = node.Get("issuedAt")?.AsLong()         ?? 0;
      lease.ExpiresAt      = node.Get("expiresAt")?.AsLong()        ?? 0;
      lease.Status         = node.Get("status")?.AsString()         ?? "";
      lease.Signature      = node.Get("signature")?.AsString()      ?? "";

      var entsNode = node.Get("entitlements")?.AsArray();
      if (entsNode != null) {
        var ents = new string[entsNode.Count];
        for (int i = 0; i < entsNode.Count; i++)
          ents[i] = entsNode[i].AsString() ?? "";
        lease.Entitlements = ents;
      } else {
        lease.Entitlements = Array.Empty<string>();
      }
      return lease;
    }

    /// <summary>
    /// Serialize a Lease to a JsonValue (for CachedState storage).
    /// Uses camelCase keys to match the server's wire format.
    /// </summary>
    internal static JsonValue LeaseToJsonValue(Lease lease) {
      var entsArr = new List<JsonValue>();
      foreach (var e in lease.Entitlements ?? Array.Empty<string>())
        entsArr.Add(JsonValue.MakeString(e));

      var obj = new Dictionary<string, JsonValue> {
        ["kid"]            = JsonValue.MakeString(lease.Kid),
        ["licenseKeyHash"] = JsonValue.MakeString(lease.LicenseKeyHash),
        ["instanceId"]     = JsonValue.MakeString(lease.InstanceId),
        ["issuedAt"]       = JsonValue.MakeNumber(lease.IssuedAt.ToString(CultureInfo.InvariantCulture)),
        ["expiresAt"]      = JsonValue.MakeNumber(lease.ExpiresAt.ToString(CultureInfo.InvariantCulture)),
        ["status"]         = JsonValue.MakeString(lease.Status),
        ["entitlements"]   = JsonValue.MakeArray(entsArr),
        ["signature"]      = JsonValue.MakeString(lease.Signature)
      };
      return JsonValue.MakeObject(obj);
    }
  }
}
