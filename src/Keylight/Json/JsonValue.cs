using System;
using System.Collections.Generic;
using System.Globalization;

namespace Keylight.Json {
  /// <summary>
  /// Minimal immutable JSON value tree.
  /// Internal use only — not part of the public API.
  /// </summary>
  internal sealed class JsonValue {
    private enum JKind { Object, Array, String, Number, Bool, Null }

    private readonly JKind _kind;
    // One of these is set based on kind:
    private readonly IDictionary<string, JsonValue>? _obj;
    private readonly IList<JsonValue>? _arr;
    private readonly string? _str;   // String value or raw number text
    private readonly bool _bool;

    // --- Internal constructors ---

    private JsonValue(IDictionary<string, JsonValue> obj) {
      _kind = JKind.Object;
      _obj = obj;
    }
    private JsonValue(IList<JsonValue> arr) {
      _kind = JKind.Array;
      _arr = arr;
    }
    private JsonValue(string s, bool isNumber) {
      _kind = isNumber ? JKind.Number : JKind.String;
      _str = s;
    }
    private JsonValue(bool b) {
      _kind = JKind.Bool;
      _bool = b;
    }
    private JsonValue() {
      _kind = JKind.Null;
    }

    // --- Factories ---

    internal static JsonValue MakeObject(IDictionary<string, JsonValue> obj) => new JsonValue(obj);
    internal static JsonValue MakeArray(IList<JsonValue> arr) => new JsonValue(arr);
    internal static JsonValue MakeString(string s) => new JsonValue(s, isNumber: false);
    internal static JsonValue MakeNumber(string raw) => new JsonValue(raw, isNumber: true);
    internal static JsonValue MakeBool(bool b) => new JsonValue(b);
    internal static readonly JsonValue MakeNull = new JsonValue();

    // --- Accessors ---

    /// <summary>True if this value is JSON null.</summary>
    public bool IsNull => _kind == JKind.Null;

    /// <summary>Returns the string value, or null if not a string token.</summary>
    public string? AsString() => _kind == JKind.String ? _str : null;

    /// <summary>Returns the numeric value as long, or null if not a number.</summary>
    public long? AsLong() {
      if (_kind != JKind.Number || _str == null) return null;
      if (long.TryParse(_str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        return l;
      // Handle float representation of whole numbers (e.g. "1.23456789E9")
      if (double.TryParse(_str, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        return (long)d;
      return null;
    }

    /// <summary>Returns the numeric value as double, or null if not a number.</summary>
    public double? AsDouble() {
      if (_kind != JKind.Number || _str == null) return null;
      if (double.TryParse(_str, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        return v;
      return null;
    }

    /// <summary>Returns the bool value, or null if not a boolean token.</summary>
    public bool? AsBool() => _kind == JKind.Bool ? (bool?)_bool : null;

    /// <summary>Object property lookup. Returns false if this is not an object or the key is absent.</summary>
    public bool TryGet(string key, out JsonValue? value) {
      if (_kind == JKind.Object && _obj != null && _obj.TryGetValue(key, out var v)) {
        value = v;
        return true;
      }
      value = null;
      return false;
    }

    /// <summary>Object property lookup. Returns null if not an object or key is absent.</summary>
    public JsonValue? Get(string key) {
      if (_kind == JKind.Object && _obj != null && _obj.TryGetValue(key, out var v))
        return v;
      return null;
    }

    /// <summary>Returns the array, or null if this is not an array token.</summary>
    public IList<JsonValue>? AsArray() => _kind == JKind.Array ? _arr : null;

    /// <summary>Returns the object dictionary, or null if this is not an object token.</summary>
    public IDictionary<string, JsonValue>? AsObject() => _kind == JKind.Object ? _obj : null;
  }
}
