using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DSPPyPort.Core
{
    internal enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>
    /// 零依赖 JSON 值。
    /// 内容包契约固定且结构简单，不为此引入第三方 JSON 库（本机 Managed / BepInEx core 都没有
    /// Newtonsoft.Json），自己实现可以给出带行号的错误信息，生成器写错时更容易定位。
    /// </summary>
    internal sealed class JsonValue
    {
        internal JsonKind Kind;
        internal bool BoolValue;
        internal double NumberValue;
        internal string StringValue;
        internal List<JsonValue> ArrayValue;
        internal Dictionary<string, JsonValue> ObjectValue;

        internal bool IsNull { get { return Kind == JsonKind.Null; } }
        internal bool IsObject { get { return Kind == JsonKind.Object; } }
        internal bool IsArray { get { return Kind == JsonKind.Array; } }

        internal int Count { get { return ArrayValue != null ? ArrayValue.Count : 0; } }

        internal bool Has(string key)
        {
            return ObjectValue != null && ObjectValue.ContainsKey(key);
        }

        internal JsonValue Get(string key)
        {
            JsonValue value;
            if (ObjectValue != null && ObjectValue.TryGetValue(key, out value))
            {
                return value;
            }
            return null;
        }

        // ---------------------------------------------------------------- 标量转换
        internal string AsString(string fallback)
        {
            switch (Kind)
            {
                case JsonKind.String: return StringValue;
                case JsonKind.Number: return NumberValue.ToString(CultureInfo.InvariantCulture);
                case JsonKind.Bool: return BoolValue ? "true" : "false";
                default: return fallback;
            }
        }

        internal int AsInt(int fallback)
        {
            switch (Kind)
            {
                case JsonKind.Number:
                    if (double.IsNaN(NumberValue) || double.IsInfinity(NumberValue))
                    {
                        return fallback;
                    }
                    return (int)Math.Round(NumberValue);
                case JsonKind.String:
                    {
                        int parsed;
                        if (int.TryParse(StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        {
                            return parsed;
                        }
                        return fallback;
                    }
                case JsonKind.Bool:
                    return BoolValue ? 1 : 0;
                default:
                    return fallback;
            }
        }

        internal long AsLong(long fallback)
        {
            switch (Kind)
            {
                case JsonKind.Number:
                    if (double.IsNaN(NumberValue) || double.IsInfinity(NumberValue))
                    {
                        return fallback;
                    }
                    return (long)Math.Round(NumberValue);
                case JsonKind.String:
                    {
                        long parsed;
                        if (long.TryParse(StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        {
                            return parsed;
                        }
                        return fallback;
                    }
                case JsonKind.Bool:
                    return BoolValue ? 1L : 0L;
                default:
                    return fallback;
            }
        }

        internal double AsDouble(double fallback)
        {
            switch (Kind)
            {
                case JsonKind.Number:
                    return NumberValue;
                case JsonKind.String:
                    {
                        double parsed;
                        if (double.TryParse(StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                        {
                            return parsed;
                        }
                        return fallback;
                    }
                case JsonKind.Bool:
                    return BoolValue ? 1.0 : 0.0;
                default:
                    return fallback;
            }
        }

        internal bool AsBool(bool fallback)
        {
            switch (Kind)
            {
                case JsonKind.Bool: return BoolValue;
                case JsonKind.Number: return Math.Abs(NumberValue) > 0.0000001;
                case JsonKind.String:
                    {
                        bool parsed;
                        if (bool.TryParse(StringValue, out parsed))
                        {
                            return parsed;
                        }
                        if (StringValue == "1")
                        {
                            return true;
                        }
                        if (StringValue == "0")
                        {
                            return false;
                        }
                        return fallback;
                    }
                default:
                    return fallback;
            }
        }

        // ---------------------------------------------------------------- 对象字段访问
        internal string Str(string key, string fallback)
        {
            JsonValue value = Get(key);
            return value == null || value.IsNull ? fallback : value.AsString(fallback);
        }

        internal int Int(string key, int fallback)
        {
            JsonValue value = Get(key);
            return value == null || value.IsNull ? fallback : value.AsInt(fallback);
        }

        internal long Long(string key, long fallback)
        {
            JsonValue value = Get(key);
            return value == null || value.IsNull ? fallback : value.AsLong(fallback);
        }

        internal double Dbl(string key, double fallback)
        {
            JsonValue value = Get(key);
            return value == null || value.IsNull ? fallback : value.AsDouble(fallback);
        }

        internal bool Bool(string key, bool fallback)
        {
            JsonValue value = Get(key);
            return value == null || value.IsNull ? fallback : value.AsBool(fallback);
        }

        internal JsonValue Node(string key)
        {
            JsonValue value = Get(key);
            return value == null || value.IsNull ? null : value;
        }

        internal List<JsonValue> Nodes()
        {
            if (ArrayValue != null)
            {
                return ArrayValue;
            }
            return new List<JsonValue>(0);
        }

        internal List<string> StrList(string key)
        {
            List<string> result = new List<string>();
            JsonValue value = Node(key);
            if (value == null)
            {
                return result;
            }
            if (value.Kind == JsonKind.String)
            {
                result.Add(value.StringValue);
                return result;
            }
            if (value.Kind != JsonKind.Array)
            {
                return result;
            }
            for (int i = 0; i < value.ArrayValue.Count; i++)
            {
                JsonValue item = value.ArrayValue[i];
                if (item != null && !item.IsNull)
                {
                    result.Add(item.AsString(null));
                }
            }
            return result;
        }

        internal List<int> IntList(string key)
        {
            List<int> result = new List<int>();
            JsonValue value = Node(key);
            if (value == null)
            {
                return result;
            }
            if (value.Kind == JsonKind.Array)
            {
                for (int i = 0; i < value.ArrayValue.Count; i++)
                {
                    JsonValue item = value.ArrayValue[i];
                    result.Add(item == null || item.IsNull ? 0 : item.AsInt(0));
                }
                return result;
            }
            result.Add(value.AsInt(0));
            return result;
        }

        internal Dictionary<string, string> StrMap()
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (ObjectValue == null)
            {
                return result;
            }
            foreach (KeyValuePair<string, JsonValue> pair in ObjectValue)
            {
                if (pair.Value == null || pair.Value.Kind != JsonKind.String)
                {
                    continue;
                }
                result[pair.Key] = pair.Value.StringValue;
            }
            return result;
        }
    }

    internal sealed class JsonParseException : Exception
    {
        internal JsonParseException(string message) : base(message)
        {
        }
    }

    /// <summary>递归下降 JSON 解析器（对象/数组/字符串/数字/布尔/null）。</summary>
    internal sealed class JsonReader
    {
        private readonly string text;
        private int pos;

        private JsonReader(string text)
        {
            this.text = text;
        }

        internal static JsonValue Parse(string json)
        {
            if (json == null)
            {
                throw new JsonParseException("JSON 文本为 null");
            }
            if (json.Length > 0 && json[0] == '\uFEFF')
            {
                json = json.Substring(1);
            }
            JsonReader reader = new JsonReader(json);
            reader.SkipWhitespace();
            JsonValue value = reader.ParseValue();
            reader.SkipWhitespace();
            if (reader.pos < reader.text.Length)
            {
                throw reader.Error("根值之后还有多余内容");
            }
            return value;
        }

        private JsonParseException Error(string message)
        {
            int line = 1;
            int column = 1;
            for (int i = 0; i < pos && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }
            return new JsonParseException(string.Format("JSON 解析失败（第 {0} 行第 {1} 列）：{2}", line, column, message));
        }

        private void SkipWhitespace()
        {
            while (pos < text.Length)
            {
                char c = text[pos];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    pos++;
                    continue;
                }
                break;
            }
        }

        private char Peek()
        {
            if (pos >= text.Length)
            {
                throw Error("意外的文件结尾");
            }
            return text[pos];
        }

        private void Expect(char expected)
        {
            if (pos >= text.Length || text[pos] != expected)
            {
                throw Error("期望字符 '" + expected + "'");
            }
            pos++;
        }

        private JsonValue ParseValue()
        {
            char c = Peek();
            switch (c)
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"':
                    {
                        JsonValue value = new JsonValue();
                        value.Kind = JsonKind.String;
                        value.StringValue = ParseString();
                        return value;
                    }
                case 't':
                    ExpectLiteral("true");
                    return NewBool(true);
                case 'f':
                    ExpectLiteral("false");
                    return NewBool(false);
                case 'n':
                    ExpectLiteral("null");
                    return new JsonValue { Kind = JsonKind.Null };
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                    {
                        return ParseNumber();
                    }
                    throw Error("无法识别的 JSON 值起始字符 '" + c + "'");
            }
        }

        private static JsonValue NewBool(bool value)
        {
            return new JsonValue { Kind = JsonKind.Bool, BoolValue = value };
        }

        private void ExpectLiteral(string literal)
        {
            if (pos + literal.Length > text.Length || string.CompareOrdinal(text, pos, literal, 0, literal.Length) != 0)
            {
                throw Error("期望字面量 '" + literal + "'");
            }
            pos += literal.Length;
        }

        private JsonValue ParseObject()
        {
            Expect('{');
            JsonValue value = new JsonValue();
            value.Kind = JsonKind.Object;
            value.ObjectValue = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            SkipWhitespace();
            if (pos < text.Length && text[pos] == '}')
            {
                pos++;
                return value;
            }
            while (true)
            {
                SkipWhitespace();
                if (pos >= text.Length || text[pos] != '"')
                {
                    throw Error("对象键必须是字符串");
                }
                string key = ParseString();
                SkipWhitespace();
                Expect(':');
                SkipWhitespace();
                JsonValue member = ParseValue();
                value.ObjectValue[key] = member;
                SkipWhitespace();
                char next = Peek();
                if (next == ',')
                {
                    pos++;
                    continue;
                }
                if (next == '}')
                {
                    pos++;
                    return value;
                }
                throw Error("对象缺少 ',' 或 '}'");
            }
        }

        private JsonValue ParseArray()
        {
            Expect('[');
            JsonValue value = new JsonValue();
            value.Kind = JsonKind.Array;
            value.ArrayValue = new List<JsonValue>();
            SkipWhitespace();
            if (pos < text.Length && text[pos] == ']')
            {
                pos++;
                return value;
            }
            while (true)
            {
                SkipWhitespace();
                value.ArrayValue.Add(ParseValue());
                SkipWhitespace();
                char next = Peek();
                if (next == ',')
                {
                    pos++;
                    continue;
                }
                if (next == ']')
                {
                    pos++;
                    return value;
                }
                throw Error("数组缺少 ',' 或 ']'");
            }
        }

        private string ParseString()
        {
            Expect('"');
            StringBuilder builder = new StringBuilder();
            while (true)
            {
                if (pos >= text.Length)
                {
                    throw Error("字符串未闭合");
                }
                char c = text[pos++];
                if (c == '"')
                {
                    return builder.ToString();
                }
                if (c != '\\')
                {
                    if (c < ' ')
                    {
                        throw Error("字符串里存在未转义的控制字符");
                    }
                    builder.Append(c);
                    continue;
                }
                if (pos >= text.Length)
                {
                    throw Error("转义序列未完成");
                }
                char escape = text[pos++];
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
                        {
                            if (pos + 4 > text.Length)
                            {
                                throw Error("\\u 转义不完整");
                            }
                            string hex = text.Substring(pos, 4);
                            pos += 4;
                            int code;
                            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            {
                                throw Error("\\u 转义不是合法十六进制：" + hex);
                            }
                            builder.Append((char)code);
                            break;
                        }
                    default:
                        throw Error("未知转义字符 '\\" + escape + "'");
                }
            }
        }

        private JsonValue ParseNumber()
        {
            int start = pos;
            if (pos < text.Length && text[pos] == '-')
            {
                pos++;
            }
            while (pos < text.Length && text[pos] >= '0' && text[pos] <= '9')
            {
                pos++;
            }
            if (pos < text.Length && text[pos] == '.')
            {
                pos++;
                while (pos < text.Length && text[pos] >= '0' && text[pos] <= '9')
                {
                    pos++;
                }
            }
            if (pos < text.Length && (text[pos] == 'e' || text[pos] == 'E'))
            {
                pos++;
                if (pos < text.Length && (text[pos] == '+' || text[pos] == '-'))
                {
                    pos++;
                }
                while (pos < text.Length && text[pos] >= '0' && text[pos] <= '9')
                {
                    pos++;
                }
            }
            string token = text.Substring(start, pos - start);
            double parsed;
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                throw Error("非法数字 '" + token + "'");
            }
            return new JsonValue { Kind = JsonKind.Number, NumberValue = parsed };
        }
    }
}
