using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace CodexControlCenter.Core;

/// <summary>
/// Small, targeted TOML editor. It changes scalar spans in the parsed syntax tree and
/// validates the resulting document before returning it, so comments, ordering and
/// unknown keys remain untouched.
/// </summary>
internal static class TomlEditor
{
    public static void Validate(string content, string sourceName)
    {
        if (content.Contains('\0'))
        {
            throw new FormatException($"{sourceName}: TOML contains a NUL character.");
        }

        DocumentSyntax document;
        try
        {
            document = SyntaxParser.ParseStrict(content, sourceName, true);
        }
        catch (Exception ex) when (ex is not FormatException)
        {
            throw new FormatException($"{sourceName}: invalid TOML.", ex);
        }

        if (document.HasErrors)
        {
            var first = document.Diagnostics.FirstOrDefault(x => x.Kind == DiagnosticMessageKind.Error);
            var location = first is null ? "" : $" ({first.Span.Start.Line}:{first.Span.Start.Column})";
            throw new FormatException($"{sourceName}: invalid TOML{location}.");
        }
    }

    public static TomlTable ReadTable(string content, string sourceName)
    {
        Validate(content, sourceName);
        return TomlSerializer.Deserialize<TomlTable>(content, new TomlSerializerOptions
        {
            DuplicateKeyHandling = TomlDuplicateKeyHandling.Error
        }) ?? new TomlTable();
    }

    public static string SetValue(string content, string? tablePath, string key, string value, string sourceName)
    {
        return SetScalar(content, tablePath, key, FormatString(value), sourceName);
    }

    public static string SetValue(string content, string? tablePath, string key, long value, string sourceName)
    {
        return SetScalar(content, tablePath, key, value.ToString(CultureInfo.InvariantCulture), sourceName);
    }

    public static string SetValue(string content, string? tablePath, string key, bool value, string sourceName)
    {
        return SetScalar(content, tablePath, key, value ? "true" : "false", sourceName);
    }

    public static string SetValues(string content, string? tablePath, IReadOnlyList<TomlValueAssignment> values, string sourceName)
    {
        foreach (var value in values)
        {
            content = SetScalar(content, tablePath, value.Key, value.FormattedValue, sourceName);
        }

        return content;
    }

    // Hook state keys contain periods and backslashes; each path segment must stay literal.
    public static string SetNamedTableValue(string content, IReadOnlyList<string> segments, string key, string value, string sourceName)
    {
        Validate(content,sourceName);
        var document=Parse(content);
        var table=document.Tables.FirstOrDefault(t=>new[]{KeyPart(t.Name?.Key)}.Concat(t.Name?.DotKeys.Select(k=>KeyPart(k.Key))??[]).SequenceEqual(segments));
        string formatted=FormatString(value);
        var pair=table?.Items.FirstOrDefault(item=>KeyName(item.Key)==key);
        if(pair?.Value is not null){var span=GetScalarToken(pair.Value).Span;return content[..span.Offset]+formatted+content[(span.Offset+span.Length)..];}
        string newline=content.Contains("\r\n",StringComparison.Ordinal)?"\r\n":"\n";
        string line=FormatBareKey(key)+" = "+formatted+newline;
        if(table is null)return content+(content.EndsWith('\n')?"":newline)+newline+"["+string.Join(".",segments.Select(FormatBareKey))+"]"+newline+line;
        int offset=table.Span.Offset+table.Span.Length;
        return content[..offset]+(offset>0&&content[offset-1]!='\n'?newline:"")+line+content[offset..];
    }

    public static string EnsureTable(string content, string tablePath, string sourceName)
    {
        Validate(content, sourceName);
        var document = SyntaxParser.ParseStrict(content, sourceName, true);
        if (FindTable(document, tablePath) is not null)
        {
            return content;
        }

        var header = $"[{FormatDottedKey(tablePath)}]";
        var prefix = content.Length == 0 || content.EndsWith('\n') ? "" : Environment.NewLine;
        var suffix = content.Length == 0 ? "" : (content.EndsWith("\n\n") ? "" : Environment.NewLine);
        return content + prefix + header + Environment.NewLine + suffix;
    }

    public static bool TryGetString(string content, string? tablePath, string key, out string value)
    {
        value = "";
        var document = Parse(content);
        var pair = FindPair(document, tablePath, key);
        if (pair?.Value is StringValueSyntax stringValue)
        {
            value = stringValue.Value ?? "";
            return true;
        }

        return false;
    }

    public static bool TryGetInt64(string content, string? tablePath, string key, out long value)
    {
        value = 0;
        var document = Parse(content);
        var pair = FindPair(document, tablePath, key);
        if (pair?.Value is IntegerValueSyntax integerValue)
        {
            value = integerValue.Value;
            return true;
        }

        return false;
    }

    public static bool TryGetBoolean(string content, string? tablePath, string key, out bool value)
    {
        value = false;
        var document = Parse(content);
        var pair = FindPair(document, tablePath, key);
        if (pair?.Value is BooleanValueSyntax booleanValue)
        {
            value = booleanValue.Value;
            return true;
        }

        return false;
    }

    private static string SetScalar(string content, string? tablePath, string key, string formattedValue, string sourceName)
    {
        Validate(content, sourceName);
        var document = Parse(content);
        var pair = FindPair(document, tablePath, key);
        if (pair?.Value is not null)
        {
            var token = GetScalarToken(pair.Value);
            var span = token.Span;
            return content[..span.Offset] + formattedValue + content[(span.Offset + span.Length)..];
        }

        var line = $"{FormatBareKey(key)} = {formattedValue}{Environment.NewLine}";
        if (tablePath is null)
        {
            var firstTable = document.Tables.FirstOrDefault();
            var insertAt = firstTable?.Span.Offset ?? content.Length;
            return content[..insertAt] + line + content[insertAt..];
        }

        var table = FindTable(document, tablePath);
        if (table is null)
        {
            content = EnsureTable(content, tablePath, sourceName);
            document = Parse(content);
            table = FindTable(document, tablePath) ?? throw new InvalidOperationException("Unable to create TOML table.");
        }

        var offset = table.Span.Offset + table.Span.Length;
        if (offset < content.Length && content[offset - 1] != '\n')
        {
            line = Environment.NewLine + line;
        }

        return content[..offset] + line + content[offset..];
    }

    private static SyntaxToken GetScalarToken(ValueSyntax value)
    {
        if (value is StringValueSyntax stringValue)
        {
            return stringValue.Token!;
        }

        if (value is IntegerValueSyntax integerValue)
        {
            return integerValue.Token!;
        }

        if (value is FloatValueSyntax floatValue)
        {
            return floatValue.Token!;
        }

        if (value is BooleanValueSyntax booleanValue)
        {
            return booleanValue.Token!;
        }

        if (value is DateTimeValueSyntax dateValue)
        {
            return dateValue.Token!;
        }

        return value.Descendants(false).OfType<SyntaxToken>().First(x => !x.TokenKind.IsTrivia());
    }

    private static DocumentSyntax Parse(string content)
    {
        return SyntaxParser.ParseStrict(content, "config.toml", true);
    }

    private static KeyValueSyntax? FindPair(DocumentSyntax document, string? tablePath, string key)
    {
        var items = tablePath is null
            ? document.KeyValues
            : FindTable(document, tablePath)?.Items;

        if (items is null)
        {
            return null;
        }

        return items.FirstOrDefault(x => KeyName(x.Key) == key);
    }

    private static TableSyntaxBase? FindTable(DocumentSyntax document, string tablePath)
    {
        return document.Tables.FirstOrDefault(x => string.Equals(TableName(x.Name), tablePath, StringComparison.Ordinal));
    }

    private static string TableName(KeySyntax? key)
    {
        if (key is null)
        {
            return "";
        }

        var parts = new List<string> { KeyPart(key.Key) };
        parts.AddRange(key.DotKeys.Select(x => KeyPart(x.Key)));
        return string.Join(".", parts);
    }

    private static string KeyName(KeySyntax? key)
    {
        return key is null ? "" : KeyPart(key.Key);
    }

    private static string KeyPart(BareKeyOrStringValueSyntax? part)
    {
        return part switch
        {
            BareKeySyntax bare => bare.Key?.Text ?? "",
            StringValueSyntax stringValue => stringValue.Value ?? "",
            _ => part?.ToString() ?? ""
        };
    }

    private static string FormatBareKey(string key)
    {
        return Regex.IsMatch(key, "^[A-Za-z0-9_-]+$") ? key : FormatString(key);
    }

    private static string FormatDottedKey(string path)
    {
        return string.Join(".", path.Split('.', StringSplitOptions.None).Select(FormatBareKey));
    }

    private static string FormatString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(ch))
                    {
                        builder.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(ch);
                    }
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    internal sealed record TomlValueAssignment(string Key, string FormattedValue)
    {
        public static TomlValueAssignment String(string key, string value) => new(key, FormatString(value));
        public static TomlValueAssignment Integer(string key, long value) => new(key, value.ToString(CultureInfo.InvariantCulture));
        public static TomlValueAssignment Boolean(string key, bool value) => new(key, value ? "true" : "false");
    }
}




