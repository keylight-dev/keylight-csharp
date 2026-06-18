using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Keylight.Json {
  /// <summary>
  /// Minimal zero-dependency JSON reader and writer.
  /// Internal use only — not part of the public API.
  /// </summary>
  internal static class JsonCodec {
    // -------------------------------------------------------------------------
    // READER
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parse a JSON string into a <see cref="JsonValue"/> tree.
    /// Returns null on malformed input — never throws on bad JSON.
    /// </summary>
    public static JsonValue? Parse(string? text) {
      if (text == null) return null;
      try {
        var reader = new Reader(text);
        var value = reader.ReadValue();
        reader.SkipWhitespace();
        if (!reader.IsEnd) return null; // trailing garbage
        return value;
      } catch {
        return null;
      }
    }

    // -------------------------------------------------------------------------
    // WRITER
    // -------------------------------------------------------------------------

    /// <summary>
    /// Serialize a string→object? dictionary to compact JSON,
    /// omitting keys whose value is null.
    /// Supported value types: string, bool, long, int, double, float, null.
    /// </summary>
    public static string Stringify(IDictionary<string, object?> obj) {
      var sb = new StringBuilder();
      WriteObject(sb, obj);
      return sb.ToString();
    }

    private static void WriteObject(StringBuilder sb, IDictionary<string, object?> obj) {
      sb.Append('{');
      bool first = true;
      foreach (var kv in obj) {
        if (kv.Value == null) continue; // omit null values
        if (!first) sb.Append(',');
        first = false;
        sb.Append('"');
        WriteEscapedString(sb, kv.Key);
        sb.Append("\":");
        WriteValue(sb, kv.Value);
      }
      sb.Append('}');
    }

    private static void WriteValue(StringBuilder sb, object? value) {
      switch (value) {
        case null:
          sb.Append("null");
          break;
        case bool b:
          sb.Append(b ? "true" : "false");
          break;
        case long l:
          sb.Append(l.ToString(CultureInfo.InvariantCulture));
          break;
        case int i:
          sb.Append(i.ToString(CultureInfo.InvariantCulture));
          break;
        case double d:
          // Use integer form when applicable
          if (d == Math.Floor(d) && !double.IsInfinity(d) && !double.IsNaN(d))
            sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
          else
            sb.Append(d.ToString("G", CultureInfo.InvariantCulture));
          break;
        case float f:
          if (f == Math.Floor(f) && !float.IsInfinity(f) && !float.IsNaN(f))
            sb.Append(((long)f).ToString(CultureInfo.InvariantCulture));
          else
            sb.Append(f.ToString("G", CultureInfo.InvariantCulture));
          break;
        case string s:
          sb.Append('"');
          WriteEscapedString(sb, s);
          sb.Append('"');
          break;
        default:
          // Fallback — shouldn't happen in our usage
          sb.Append('"');
          WriteEscapedString(sb, value.ToString() ?? "");
          sb.Append('"');
          break;
      }
    }

    internal static void WriteEscapedString(StringBuilder sb, string s) {
      foreach (var c in s) {
        switch (c) {
          case '"':  sb.Append("\\\""); break;
          case '\\': sb.Append("\\\\"); break;
          case '\b': sb.Append("\\b");  break;
          case '\f': sb.Append("\\f");  break;
          case '\n': sb.Append("\\n");  break;
          case '\r': sb.Append("\\r");  break;
          case '\t': sb.Append("\\t");  break;
          default:
            if (c < 0x20) {
              sb.Append("\\u");
              sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            } else {
              sb.Append(c);
            }
            break;
        }
      }
    }

    // -------------------------------------------------------------------------
    // WRITER: serialize a JsonValue tree back to JSON (used by Store)
    // -------------------------------------------------------------------------

    internal static string StringifyValue(JsonValue value) {
      var sb = new StringBuilder();
      WriteJsonValue(sb, value);
      return sb.ToString();
    }

    private static void WriteJsonValue(StringBuilder sb, JsonValue? value) {
      if (value == null || value.IsNull) {
        sb.Append("null");
        return;
      }
      // Object
      var obj = value.AsObject();
      if (obj != null) {
        sb.Append('{');
        bool first = true;
        foreach (var kv in obj) {
          if (!first) sb.Append(',');
          first = false;
          sb.Append('"');
          WriteEscapedString(sb, kv.Key);
          sb.Append("\":");
          WriteJsonValue(sb, kv.Value);
        }
        sb.Append('}');
        return;
      }
      // Array
      var arr = value.AsArray();
      if (arr != null) {
        sb.Append('[');
        for (int i = 0; i < arr.Count; i++) {
          if (i > 0) sb.Append(',');
          WriteJsonValue(sb, arr[i]);
        }
        sb.Append(']');
        return;
      }
      // String
      var s = value.AsString();
      if (s != null) {
        sb.Append('"');
        WriteEscapedString(sb, s);
        sb.Append('"');
        return;
      }
      // Bool
      var b = value.AsBool();
      if (b.HasValue) {
        sb.Append(b.Value ? "true" : "false");
        return;
      }
      // Number
      var d = value.AsDouble();
      if (d.HasValue) {
        // Prefer integer representation
        if (d.Value == Math.Floor(d.Value) && !double.IsInfinity(d.Value) && !double.IsNaN(d.Value))
          sb.Append(((long)d.Value).ToString(CultureInfo.InvariantCulture));
        else
          sb.Append(d.Value.ToString("G", CultureInfo.InvariantCulture));
        return;
      }
      // Null (handled above, this is a safety fallback)
      sb.Append("null");
    }

    // -------------------------------------------------------------------------
    // READER IMPLEMENTATION
    // -------------------------------------------------------------------------

    private sealed class Reader {
      private readonly string _text;
      private int _pos;

      internal Reader(string text) {
        _text = text;
        _pos = 0;
      }

      internal bool IsEnd => _pos >= _text.Length;

      internal void SkipWhitespace() {
        while (_pos < _text.Length && (_text[_pos] == ' ' || _text[_pos] == '\t' ||
               _text[_pos] == '\n' || _text[_pos] == '\r'))
          _pos++;
      }

      private char Peek() {
        SkipWhitespace();
        if (_pos >= _text.Length) throw new FormatException("Unexpected end of JSON");
        return _text[_pos];
      }

      private char Consume() {
        SkipWhitespace();
        if (_pos >= _text.Length) throw new FormatException("Unexpected end of JSON");
        return _text[_pos++];
      }

      private void Expect(char c) {
        var got = Consume();
        if (got != c) throw new FormatException($"Expected '{c}' but got '{got}' at position {_pos - 1}");
      }

      internal JsonValue? ReadValue() {
        var c = Peek();
        switch (c) {
          case '{': return ReadObject();
          case '[': return ReadArray();
          case '"': return ReadString();
          case 't': return ReadLiteral("true",  JsonValue.MakeBool(true));
          case 'f': return ReadLiteral("false", JsonValue.MakeBool(false));
          case 'n': return ReadLiteral("null",  JsonValue.MakeNull);
          default:
            if (c == '-' || (c >= '0' && c <= '9'))
              return ReadNumber();
            throw new FormatException($"Unexpected character '{c}' at position {_pos}");
        }
      }

      private JsonValue ReadObject() {
        Expect('{');
        var obj = new Dictionary<string, JsonValue>();
        SkipWhitespace();
        if (Peek() == '}') {
          _pos++;
          return JsonValue.MakeObject(obj);
        }
        while (true) {
          SkipWhitespace();
          // Read key
          if (Peek() != '"') throw new FormatException($"Expected '\"' for object key at {_pos}");
          var keyVal = ReadString();
          var key = keyVal.AsString()!;
          SkipWhitespace();
          Expect(':');
          var value = ReadValue();
          if (value == null) throw new FormatException("Null value in object");
          obj[key] = value;
          SkipWhitespace();
          var next = Peek();
          if (next == '}') { _pos++; break; }
          if (next == ',') { _pos++; continue; }
          throw new FormatException($"Expected ',' or '}}' in object at {_pos}");
        }
        return JsonValue.MakeObject(obj);
      }

      private JsonValue ReadArray() {
        Expect('[');
        var arr = new List<JsonValue>();
        SkipWhitespace();
        if (Peek() == ']') {
          _pos++;
          return JsonValue.MakeArray(arr);
        }
        while (true) {
          var value = ReadValue();
          if (value == null) throw new FormatException("Null value in array");
          arr.Add(value);
          SkipWhitespace();
          var next = Peek();
          if (next == ']') { _pos++; break; }
          if (next == ',') { _pos++; continue; }
          throw new FormatException($"Expected ',' or ']' in array at {_pos}");
        }
        return JsonValue.MakeArray(arr);
      }

      private JsonValue ReadString() {
        Expect('"');
        var sb = new StringBuilder();
        while (true) {
          if (_pos >= _text.Length) throw new FormatException("Unterminated string");
          var c = _text[_pos++];
          if (c == '"') break;
          if (c == '\\') {
            if (_pos >= _text.Length) throw new FormatException("Unterminated escape");
            var esc = _text[_pos++];
            switch (esc) {
              case '"':  sb.Append('"');  break;
              case '\\': sb.Append('\\'); break;
              case '/':  sb.Append('/');  break;
              case 'b':  sb.Append('\b'); break;
              case 'f':  sb.Append('\f'); break;
              case 'n':  sb.Append('\n'); break;
              case 'r':  sb.Append('\r'); break;
              case 't':  sb.Append('\t'); break;
              case 'u': {
                if (_pos + 4 > _text.Length) throw new FormatException("Short \\uXXXX escape");
                var hex = _text.Substring(_pos, 4);
                _pos += 4;
                if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                  throw new FormatException($"Invalid \\uXXXX: {hex}");
                sb.Append((char)code);
                break;
              }
              default:
                throw new FormatException($"Unknown escape \\{esc}");
            }
          } else {
            sb.Append(c);
          }
        }
        return JsonValue.MakeString(sb.ToString());
      }

      private JsonValue ReadNumber() {
        var start = _pos;
        // Consume optional minus
        if (_pos < _text.Length && _text[_pos] == '-') _pos++;
        // Integer part
        while (_pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9') _pos++;
        // Optional fractional
        if (_pos < _text.Length && _text[_pos] == '.') {
          _pos++;
          while (_pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9') _pos++;
        }
        // Optional exponent
        if (_pos < _text.Length && (_text[_pos] == 'e' || _text[_pos] == 'E')) {
          _pos++;
          if (_pos < _text.Length && (_text[_pos] == '+' || _text[_pos] == '-')) _pos++;
          while (_pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9') _pos++;
        }
        var raw = _text.Substring(start, _pos - start);
        if (raw.Length == 0 || raw == "-") throw new FormatException("Invalid number");
        return JsonValue.MakeNumber(raw);
      }

      private JsonValue ReadLiteral(string literal, JsonValue result) {
        if (_pos + literal.Length > _text.Length ||
            _text.Substring(_pos, literal.Length) != literal)
          throw new FormatException($"Expected literal '{literal}' at {_pos}");
        _pos += literal.Length;
        return result;
      }
    }
  }
}
