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
    /// <summary>
    /// The trial length this build was <b>configured</b> with — the seed, not the
    /// effective value. Echoing the server's own number back diagnoses nothing;
    /// the seed catches the ordinary mistake of a 30-day build running against a
    /// 14-day dashboard setting. Diagnostic only: the server must never gate on
    /// it, because a patched client sends whatever its author wants.
    /// </summary>
    public int? SdkTrialDurationDays { get; set; }

    internal string ToJson() {
      var obj = new Dictionary<string, object?> {
        ["license_key"]   = LicenseKey,
        ["instance_name"] = InstanceName,
        // Clamped here so no caller can construct an over-long field: the server
        // rejects the entire body with a 400, it does not drop the field.
        ["app_version"]   = Telemetry.Clamp(AppVersion, Telemetry.VersionMax),
        ["sdk_version"]   = Telemetry.Clamp(SdkVersion, Telemetry.VersionMax),
        ["platform"]      = Telemetry.Clamp(Platform, Telemetry.PlatformMax),
        ["sdk"]           = Telemetry.SdkId,
        ["free_tier_instance_id"] = FreeTierInstanceId,
        ["sdk_trial_duration_days"] = SdkTrialDurationDays
      };
      return JsonCodec.Stringify(obj);
    }
  }

  /// <summary>Request body for the /validate endpoint.</summary>
  public sealed class ValidateRequest {
    /// <summary>Required by the worker (validate.ts: <c>z.string().min(1)</c>);
    /// omitting it is a hard 400, not a dropped field.</summary>
    public string LicenseKey { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string? AppVersion { get; set; }
    public string? SdkVersion { get; set; }
    public string? Platform { get; set; }
    /// <summary>
    /// The trial length this build was <b>configured</b> with — the seed, not the
    /// effective value. Echoing the server's own number back diagnoses nothing;
    /// the seed catches the ordinary mistake of a 30-day build running against a
    /// 14-day dashboard setting. Diagnostic only: the server must never gate on
    /// it, because a patched client sends whatever its author wants.
    /// </summary>
    public int? SdkTrialDurationDays { get; set; }

    internal string ToJson() {
      var obj = new Dictionary<string, object?> {
        ["license_key"] = LicenseKey,
        ["instance_id"] = InstanceId,
        // See ActivateRequest.ToJson — clamped for the same reason.
        ["app_version"] = Telemetry.Clamp(AppVersion, Telemetry.VersionMax),
        ["sdk_version"] = Telemetry.Clamp(SdkVersion, Telemetry.VersionMax),
        ["platform"]    = Telemetry.Clamp(Platform, Telemetry.PlatformMax),
        ["sdk"]         = Telemetry.SdkId,
        ["sdk_trial_duration_days"] = SdkTrialDurationDays
      };
      return JsonCodec.Stringify(obj);
    }
  }

  /// <summary>Request body for the /deactivate endpoint.</summary>
  public sealed class DeactivateRequest {
    /// <summary>Required by the worker (deactivate.ts: <c>z.string().min(1)</c>).</summary>
    public string LicenseKey { get; set; } = "";
    public string InstanceId { get; set; } = "";

    internal string ToJson() {
      var obj = new Dictionary<string, object?> {
        ["license_key"] = LicenseKey,
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
      resp.LicenseExpiresAt = root.Get("license_expires_at")?.AsLong();
      resp.Lease = WireHelpers.ParseLease(root.Get("lease"));
      return resp;
    }
  }

  /// <summary>Response body from the /validate endpoint.</summary>
  public sealed class ValidateResponse {
    public bool Valid { get; set; }
    public long? LicenseExpiresAt { get; set; }
    public Lease? Lease { get; set; }
    public string? Error { get; set; }
    /// <summary>Server-owned trial length, riding on a call the SDK already
    /// makes. Null when the worker predates the setting — which is not the same
    /// as 0. See <see cref="ProductConfigFields"/>.</summary>
    public int? TrialDurationDays { get; set; }
    /// <summary>Server-owned free-tier flag. See <see cref="ProductConfigFields"/>.</summary>
    public bool? FreeTierEnabled { get; set; }

    /// <summary>The two settings, extracted for the client's merge step.</summary>
    public ProductConfigFields ConfigFields =>
      new ProductConfigFields { TrialDurationDays = TrialDurationDays, FreeTierEnabled = FreeTierEnabled };

    internal static ValidateResponse? Parse(string json) {
      var root = JsonCodec.Parse(json);
      if (root == null) return null;
      var resp = new ValidateResponse();
      resp.Valid = root.Get("valid")?.AsBool() ?? false;
      resp.LicenseExpiresAt = root.Get("license_expires_at")?.AsLong();
      resp.Lease = WireHelpers.ParseLease(root.Get("lease"));
      resp.Error = root.Get("error")?.AsString();
      WireHelpers.ReadConfigFields(root, out var days, out var freeTier);
      resp.TrialDurationDays = days;
      resp.FreeTierEnabled = freeTier;
      return resp;
    }
  }

  /// <summary>
  /// The two product settings the server owns, as they appear on the
  /// <c>/config</c> response and riding on <c>/validate</c>.
  /// </summary>
  /// <remarks>
  /// Both are nullable and <b>absence is meaningful</b>: null means "this install
  /// has never heard a value from the server", which is a different thing from 0
  /// or false. A tenant who turns trials off sends a real 0; collapsing that into
  /// "absent" would fall back to the compiled-in seed and silently re-enable the
  /// trial they just disabled.
  /// </remarks>
  public sealed class ProductConfigFields {
    public int? TrialDurationDays { get; set; }
    public bool? FreeTierEnabled { get; set; }

    /// <summary>True when the response carried neither setting — an older worker,
    /// which must leave a cached value alone rather than overwrite it.</summary>
    public bool IsEmpty => !TrialDurationDays.HasValue && !FreeTierEnabled.HasValue;
  }

  /// <summary>Response body from the <c>GET /config</c> endpoint.</summary>
  /// <remarks>
  /// The signature fields (<c>issued_at</c>, <c>expires_at</c>, <c>kid</c>,
  /// <c>signature</c>) are part of the frozen wire contract but are not verified
  /// by this SDK yet — they are accepted and ignored so that adding verification
  /// later is a change to this file alone.
  /// </remarks>
  public sealed class ConfigResponse {
    public int? TrialDurationDays { get; set; }
    public bool? FreeTierEnabled { get; set; }

    public ProductConfigFields ConfigFields =>
      new ProductConfigFields { TrialDurationDays = TrialDurationDays, FreeTierEnabled = FreeTierEnabled };

    internal static ConfigResponse? Parse(string json) {
      var root = JsonCodec.Parse(json);
      if (root == null) return null;
      var resp = new ConfigResponse();
      WireHelpers.ReadConfigFields(root, out var days, out var freeTier);
      resp.TrialDurationDays = days;
      resp.FreeTierEnabled = freeTier;
      return resp;
    }
  }

  internal static class WireHelpers {
    /// <summary>
    /// Read the two server-owned settings off any response body, leaving both
    /// null when the key is absent. Deliberately not defaulted to 0/false: the
    /// caller relies on null to mean "the worker said nothing".
    /// </summary>
    internal static void ReadConfigFields(JsonValue root, out int? trialDurationDays, out bool? freeTierEnabled) {
      var days = root.Get("trial_duration_days")?.AsLong();
      trialDurationDays = days.HasValue ? (int)days.Value : (int?)null;
      freeTierEnabled = root.Get("free_tier_enabled")?.AsBool();
    }

    /// <summary>
    /// Parse a Lease from a JsonValue node.
    /// Reads camelCase keys as they come from the server.
    /// </summary>
    internal static Lease? ParseLease(JsonValue? node) {
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
