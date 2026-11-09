using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace InfiniteToken.Lumber;

/// <summary>A JSON number kept as its text, from parsed JSON, so it's sent exactly as it came.</summary>
internal sealed class JsonNumber
{
    public JsonNumber(string text) => Text = text;

    public string Text { get; }

    public override string ToString() => Text;
}

/// <summary>
/// JSON without System.Text.Json, so the netstandard2.0 build (.NET Framework, Mono) needs no packages and every build
/// sends the same bytes. <see cref="Normalize(object)"/> turns any value into the few types JSON has, <see cref="Serialize"/>
/// writes them compactly as UTF-8, and <see cref="Parse"/> reads the API's answers.
/// </summary>
internal static class Json
{
    private const int MaxDepth = 64;

    // --- Normalizing ---

    /// <summary>
    /// A value as JSON's types: null, string, bool, a number (an integer type, double, float, decimal or
    /// <see cref="JsonNumber"/>), <c>List&lt;object?&gt;</c> or <c>Dictionary&lt;string, object?&gt;</c>. It never
    /// throws: what JSON has no type for goes in as text (a date as ISO 8601, an exception as its type and message), a
    /// NaN or infinity as null, an object as its public properties, and a structure that contains itself as
    /// "[Circular]".
    /// </summary>
    public static object? Normalize(object? value) => Normalize(value, new HashSet<object>(ReferenceComparer.Instance), 0);

    private static object? Normalize(object? value, HashSet<object> seen, int depth)
    {
        switch (value)
        {
            case null:
                return null;
            case string or bool or int or long or short or sbyte or byte or ushort or uint or ulong or decimal or JsonNumber:
                return value;
            case double number:
                return double.IsNaN(number) || double.IsInfinity(number) ? null : number;
            case float number:
                return float.IsNaN(number) || float.IsInfinity(number) ? null : number;
            case char character:
                return character.ToString();
            case Enum:
                return value.ToString();
            case DateTime date:
                return date.ToString("o", CultureInfo.InvariantCulture);
            case DateTimeOffset date:
                return date.ToString("o", CultureInfo.InvariantCulture);
            case TimeSpan span:
                return span.ToString("c", CultureInfo.InvariantCulture);
#if NET
            case DateOnly date:
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case TimeOnly time:
                return time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
            case System.Text.Json.JsonElement element:
                return FromJsonText(element.GetRawText());
            case System.Text.Json.JsonDocument document:
                return FromJsonText(document.RootElement.GetRawText());
            case System.Text.Json.Nodes.JsonNode node:
                return FromJsonText(node.ToJsonString());
#endif
            case Guid or Uri or Version:
                return value.ToString();
            case Exception exception:
                return ExceptionLine(exception);
            case byte[] bytes:
                return Convert.ToBase64String(bytes);
            case Type described:
                return described.FullName ?? described.Name;
            case Delegate or Assembly or MemberInfo or IntPtr or UIntPtr:
                return value.ToString();
        }

        if (depth >= MaxDepth) return "[Too deep]";
        var type = value.GetType();
        var tracked = !type.IsValueType;
        if (tracked && !seen.Add(value)) return "[Circular]";
        try
        {
            if (value is IDictionary dictionary)
            {
                var result = new Dictionary<string, object?>(dictionary.Count, StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    result[KeyText(entry.Key)] = Normalize(entry.Value, seen, depth + 1);
                }
                return result;
            }
            if (value is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var pair in pairs) result[pair.Key] = Normalize(pair.Value, seen, depth + 1);
                return result;
            }
            if (value is IEnumerable items)
            {
                if (PairsOf(type) is { } pairType) return FromPairs(items, pairType, seen, depth);
                var result = new List<object?>();
                foreach (var item in items) result.Add(Normalize(item, seen, depth + 1));
                return result;
            }
            // Everything else from the base library has its own text (an IPAddress, a StringBuilder): reading its
            // properties would send its internals
            if (type.Namespace is { } space && (space == "System" || space.StartsWith("System.", StringComparison.Ordinal)))
            {
                return value.ToString();
            }
            return FromProperties(value, type, seen, depth);
        }
        catch (Exception exception)
        {
            return "[Unreadable: " + exception.Message + "]";
        }
        finally
        {
            if (tracked) seen.Remove(value);
        }
    }

    /// <summary>An exception on one line: its type and message.</summary>
    public static string ExceptionLine(Exception exception) =>
        (exception.GetType().FullName ?? exception.GetType().Name) + ": " + exception.Message;

    // An object (a class, a record, an anonymous type) as its public instance properties
    private static object? FromProperties(object value, Type type, HashSet<object> seen, int depth)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0 || property.GetMethod?.IsPublic != true)
            {
                continue;
            }
            object? item;
            try
            {
                item = property.GetValue(value);
            }
            catch (TargetInvocationException exception)
            {
                item = "[Unreadable: " + (exception.InnerException ?? exception).Message + "]";
            }
            result[property.Name] = Normalize(item, seen, depth + 1);
        }
        return result.Count > 0 || type.IsDefined(typeof(CompilerGeneratedAttribute), false) ? result : value.ToString();
    }

    // The KeyValuePair<TKey, TValue> a collection enumerates (an IReadOnlyDictionary that isn't an IDictionary), or null
    private static Type? PairsOf(Type type)
    {
        foreach (var face in type.GetInterfaces())
        {
            if (!face.IsGenericType || face.GetGenericTypeDefinition() != typeof(IEnumerable<>)) continue;
            var item = face.GetGenericArguments()[0];
            if (item.IsGenericType && item.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)) return item;
        }
        return null;
    }

    private static Dictionary<string, object?> FromPairs(IEnumerable items, Type pairType, HashSet<object> seen, int depth)
    {
        var key = pairType.GetProperty("Key")!;
        var value = pairType.GetProperty("Value")!;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in items)
        {
            result[KeyText(key.GetValue(pair))] = Normalize(value.GetValue(pair), seen, depth + 1);
        }
        return result;
    }

    private static string KeyText(object? key) => key switch
    {
        null => "",
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => key.ToString() ?? "",
    };

    private static object? FromJsonText(string text)
    {
        try
        {
            return Parse(text);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    // --- Writing ---

    /// <summary>A normalized value as compact UTF-8 JSON.</summary>
    public static byte[] Serialize(object? value)
    {
        var builder = new StringBuilder();
        Write(builder, value);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static void Write(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case string text:
                WriteString(builder, text);
                break;
            case bool flag:
                builder.Append(flag ? "true" : "false");
                break;
            case Dictionary<string, object?> map:
                builder.Append('{');
                var first = true;
                foreach (var pair in map)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    WriteString(builder, pair.Key);
                    builder.Append(':');
                    Write(builder, pair.Value);
                }
                builder.Append('}');
                break;
            case List<object?> list:
                builder.Append('[');
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    Write(builder, list[i]);
                }
                builder.Append(']');
                break;
            case double number:
                builder.Append(double.IsNaN(number) || double.IsInfinity(number) ? "null" : number.ToString("R", CultureInfo.InvariantCulture));
                break;
            case float number:
                builder.Append(float.IsNaN(number) || float.IsInfinity(number) ? "null" : number.ToString("R", CultureInfo.InvariantCulture));
                break;
            case JsonNumber number:
                builder.Append(number.Text);
                break;
            case int or long or short or sbyte or byte or ushort or uint or ulong or decimal:
                builder.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                WriteString(builder, value.ToString() ?? "");
                break;
        }
    }

    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (c < ' ') builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }

    // --- Reading ---

    /// <summary>JSON text as normalized values (numbers as <see cref="JsonNumber"/>). Throws FormatException.</summary>
    public static object? Parse(string text)
    {
        var position = 0;
        var value = ReadValue(text, ref position, 0);
        SkipSpace(text, ref position);
        if (position != text.Length) throw new FormatException("Unexpected text after the JSON value");
        return value;
    }

    /// <summary>The API's error message (<c>{ "message": "..." }</c>), or null.</summary>
    public static string? MessageOf(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            return Parse(body!) is Dictionary<string, object?> answer && answer.TryGetValue("message", out var message)
                ? message as string
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static object? ReadValue(string text, ref int position, int depth)
    {
        if (depth > MaxDepth) throw new FormatException("JSON nested too deeply");
        SkipSpace(text, ref position);
        if (position >= text.Length) throw new FormatException("Unexpected end of JSON");
        switch (text[position])
        {
            case '{':
                {
                    position++;
                    var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                    SkipSpace(text, ref position);
                    if (Peek(text, position) == '}')
                    {
                        position++;
                        return map;
                    }
                    while (true)
                    {
                        SkipSpace(text, ref position);
                        var key = ReadString(text, ref position);
                        SkipSpace(text, ref position);
                        Expect(text, ref position, ':');
                        map[key] = ReadValue(text, ref position, depth + 1);
                        SkipSpace(text, ref position);
                        if (Peek(text, position) == ',')
                        {
                            position++;
                            continue;
                        }
                        Expect(text, ref position, '}');
                        return map;
                    }
                }
            case '[':
                {
                    position++;
                    var list = new List<object?>();
                    SkipSpace(text, ref position);
                    if (Peek(text, position) == ']')
                    {
                        position++;
                        return list;
                    }
                    while (true)
                    {
                        list.Add(ReadValue(text, ref position, depth + 1));
                        SkipSpace(text, ref position);
                        if (Peek(text, position) == ',')
                        {
                            position++;
                            continue;
                        }
                        Expect(text, ref position, ']');
                        return list;
                    }
                }
            case '"':
                return ReadString(text, ref position);
            case 't':
                ExpectWord(text, ref position, "true");
                return true;
            case 'f':
                ExpectWord(text, ref position, "false");
                return false;
            case 'n':
                ExpectWord(text, ref position, "null");
                return null;
            default:
                return ReadNumber(text, ref position);
        }
    }

    private static string ReadString(string text, ref int position)
    {
        Expect(text, ref position, '"');
        var builder = new StringBuilder();
        while (true)
        {
            if (position >= text.Length) throw new FormatException("Unterminated JSON string");
            var c = text[position++];
            if (c == '"') return builder.ToString();
            if (c < ' ') throw new FormatException("Control character in a JSON string");
            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }
            if (position >= text.Length) throw new FormatException("Unterminated JSON string");
            var escape = text[position++];
            switch (escape)
            {
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                case '/': builder.Append('/'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'u':
                    if (position + 4 > text.Length
                        || !int.TryParse(text.Substring(position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                    {
                        throw new FormatException("Bad \\u escape in a JSON string");
                    }
                    builder.Append((char)code);
                    position += 4;
                    break;
                default:
                    throw new FormatException("Bad escape in a JSON string");
            }
        }
    }

    private static JsonNumber ReadNumber(string text, ref int position)
    {
        var start = position;
        if (Peek(text, position) == '-') position++;
        var digits = position;
        while (position < text.Length && text[position] is >= '0' and <= '9') position++;
        if (position == digits || (text[digits] == '0' && position - digits > 1)) throw new FormatException("Bad JSON number");
        if (Peek(text, position) == '.')
        {
            position++;
            var fraction = position;
            while (position < text.Length && text[position] is >= '0' and <= '9') position++;
            if (position == fraction) throw new FormatException("Bad JSON number");
        }
        if (Peek(text, position) is 'e' or 'E')
        {
            position++;
            if (Peek(text, position) is '+' or '-') position++;
            var exponent = position;
            while (position < text.Length && text[position] is >= '0' and <= '9') position++;
            if (position == exponent) throw new FormatException("Bad JSON number");
        }
        return new JsonNumber(text.Substring(start, position - start));
    }

    private static char Peek(string text, int position) => position < text.Length ? text[position] : '\0';

    private static void SkipSpace(string text, ref int position)
    {
        while (position < text.Length && text[position] is ' ' or '\t' or '\n' or '\r') position++;
    }

    private static void Expect(string text, ref int position, char expected)
    {
        if (Peek(text, position) != expected) throw new FormatException("Expected '" + expected + "' in JSON");
        position++;
    }

    private static void ExpectWord(string text, ref int position, string word)
    {
        if (string.CompareOrdinal(text, position, word, 0, word.Length) != 0) throw new FormatException("Bad JSON literal");
        position += word.Length;
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
