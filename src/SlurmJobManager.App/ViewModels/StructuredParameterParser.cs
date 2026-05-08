using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlurmJobManager.App.ViewModels;

public enum StructuredParameterFileFormat
{
    Json,
    Toml,
    Ini,
}

public enum StructuredParameterValueKind
{
    String,
    Boolean,
    Integer,
    Floating,
    Null,
}

public sealed record StructuredParameterEntry(
    string Section,
    string Key,
    string[] JsonPathSegments,
    StructuredParameterValueKind ValueKind,
    string StringValue,
    bool BoolValue,
    long IntegerValue,
    double FloatingValue)
{
    public static StructuredParameterEntry FromString(string section, string key, string[] path, string value)
        => new(section, key, path, StructuredParameterValueKind.String, value, false, 0, 0d);

    public static StructuredParameterEntry FromBool(string section, string key, string[] path, bool value)
        => new(section, key, path, StructuredParameterValueKind.Boolean, string.Empty, value, 0, 0d);

    public static StructuredParameterEntry FromInteger(string section, string key, string[] path, long value)
        => new(section, key, path, StructuredParameterValueKind.Integer, string.Empty, false, value, 0d);

    public static StructuredParameterEntry FromFloating(string section, string key, string[] path, double value)
        => new(section, key, path, StructuredParameterValueKind.Floating, string.Empty, false, 0, value);

    public static StructuredParameterEntry FromNull(string section, string key, string[] path)
        => new(section, key, path, StructuredParameterValueKind.Null, string.Empty, false, 0, 0d);
}

public static class StructuredParameterParser
{
    public static bool TryParse(string remotePath, string content, out StructuredParameterFileFormat format, out List<StructuredParameterEntry> entries)
    {
        entries = new List<StructuredParameterEntry>();
        var candidates = BuildFormatCandidates(remotePath);

        foreach (var candidate in candidates)
        {
            if (TryParseByFormat(candidate, content, out entries))
            {
                format = candidate;
                return true;
            }
        }

        format = default;
        return false;
    }

    public static string Serialize(StructuredParameterFileFormat format, IReadOnlyList<StructuredParameterEntry> entries)
        => format switch
        {
            StructuredParameterFileFormat.Json => SerializeJson(entries),
            StructuredParameterFileFormat.Toml => SerializeToml(entries),
            StructuredParameterFileFormat.Ini => SerializeIni(entries),
            _ => throw new NotSupportedException($"Unsupported structured format: {format}"),
        };

    private static bool TryParseByFormat(StructuredParameterFileFormat format, string content, out List<StructuredParameterEntry> entries)
        => format switch
        {
            StructuredParameterFileFormat.Json => TryParseJson(content, out entries),
            StructuredParameterFileFormat.Toml => TryParseToml(content, out entries),
            StructuredParameterFileFormat.Ini => TryParseIni(content, out entries),
            _ => Fail(out entries),
        };

    private static bool Fail(out List<StructuredParameterEntry> entries)
    {
        entries = new List<StructuredParameterEntry>();
        return false;
    }

    private static List<StructuredParameterFileFormat> BuildFormatCandidates(string remotePath)
    {
        var candidates = new List<StructuredParameterFileFormat>();
        var ext = Path.GetExtension(remotePath)?.ToLowerInvariant() ?? string.Empty;

        switch (ext)
        {
            case ".json":
                candidates.Add(StructuredParameterFileFormat.Json);
                break;
            case ".toml":
                candidates.Add(StructuredParameterFileFormat.Toml);
                break;
            case ".ini":
            case ".cfg":
            case ".conf":
                candidates.Add(StructuredParameterFileFormat.Ini);
                break;
        }

        foreach (var f in new[]
                 {
                     StructuredParameterFileFormat.Json,
                     StructuredParameterFileFormat.Toml,
                     StructuredParameterFileFormat.Ini,
                 })
        {
            if (!candidates.Contains(f))
                candidates.Add(f);
        }

        return candidates;
    }

    private static bool TryParseJson(string content, out List<StructuredParameterEntry> entries)
    {
        entries = new List<StructuredParameterEntry>();
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            if (!CollectJsonObjectEntries(doc.RootElement, Array.Empty<string>(), entries))
                return false;

            return entries.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool CollectJsonObjectEntries(JsonElement obj, string[] path, List<StructuredParameterEntry> entries)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            var childPath = path.Concat(new[] { prop.Name }).ToArray();
            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    if (!CollectJsonObjectEntries(prop.Value, childPath, entries))
                        return false;
                    break;
                case JsonValueKind.Array:
                case JsonValueKind.Undefined:
                    return false;
                case JsonValueKind.String:
                    entries.Add(StructuredParameterEntry.FromString(BuildSection(path), prop.Name, childPath, prop.Value.GetString() ?? string.Empty));
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    entries.Add(StructuredParameterEntry.FromBool(BuildSection(path), prop.Name, childPath, prop.Value.GetBoolean()));
                    break;
                case JsonValueKind.Number:
                    if (prop.Value.TryGetInt64(out var asInt))
                        entries.Add(StructuredParameterEntry.FromInteger(BuildSection(path), prop.Name, childPath, asInt));
                    else if (prop.Value.TryGetDouble(out var asDouble))
                        entries.Add(StructuredParameterEntry.FromFloating(BuildSection(path), prop.Name, childPath, asDouble));
                    else
                        return false;
                    break;
                case JsonValueKind.Null:
                    entries.Add(StructuredParameterEntry.FromNull(BuildSection(path), prop.Name, childPath));
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool TryParseToml(string content, out List<StructuredParameterEntry> entries)
        => TryParseIniLike(content, treatHashAsComment: true, dottedSections: true, out entries);

    private static bool TryParseIni(string content, out List<StructuredParameterEntry> entries)
        => TryParseIniLike(content, treatHashAsComment: false, dottedSections: false, out entries);

    private static bool TryParseIniLike(string content, bool treatHashAsComment, bool dottedSections, out List<StructuredParameterEntry> entries)
    {
        entries = new List<StructuredParameterEntry>();
        var section = string.Empty;
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith(';'))
                continue;
            if (treatHashAsComment && line.StartsWith('#'))
                continue;

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal) && line.Length > 2)
            {
                section = line[1..^1].Trim();
                continue;
            }

            var idx = line.IndexOf('=');
            if (idx <= 0)
                return false;

            var key = line[..idx].Trim();
            var rawValue = line[(idx + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(key))
                return false;

            var pathSegments = BuildIniLikePathSegments(section, key, dottedSections);
            entries.Add(ParseIniLikeValue(section, key, pathSegments, rawValue));
        }

        return entries.Count > 0;
    }

    private static string[] BuildIniLikePathSegments(string section, string key, bool dottedSections)
    {
        if (string.IsNullOrWhiteSpace(section))
            return new[] { key };

        if (dottedSections)
            return section.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Concat(new[] { key })
                .ToArray();

        return new[] { section, key };
    }

    private static StructuredParameterEntry ParseIniLikeValue(string section, string key, string[] pathSegments, string rawValue)
    {
        if ((rawValue.StartsWith('"') && rawValue.EndsWith('"')) || (rawValue.StartsWith('\'') && rawValue.EndsWith('\'')))
            return StructuredParameterEntry.FromString(section, key, pathSegments, UnwrapQuoted(rawValue));

        if (bool.TryParse(rawValue, out var asBool))
            return StructuredParameterEntry.FromBool(section, key, pathSegments, asBool);

        if (long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asInt))
            return StructuredParameterEntry.FromInteger(section, key, pathSegments, asInt);

        if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var asFloat)
            && (rawValue.Contains('.', StringComparison.Ordinal) || rawValue.Contains('e', StringComparison.OrdinalIgnoreCase)))
            return StructuredParameterEntry.FromFloating(section, key, pathSegments, asFloat);

        if (string.Equals(rawValue, "null", StringComparison.OrdinalIgnoreCase))
            return StructuredParameterEntry.FromNull(section, key, pathSegments);

        return StructuredParameterEntry.FromString(section, key, pathSegments, rawValue);
    }

    private static string SerializeJson(IReadOnlyList<StructuredParameterEntry> entries)
    {
        var root = new JsonObject();

        foreach (var entry in entries)
        {
            if (entry.JsonPathSegments.Length == 0)
                continue;

            JsonObject cursor = root;
            for (var i = 0; i < entry.JsonPathSegments.Length - 1; i++)
            {
                var segment = entry.JsonPathSegments[i];
                if (cursor[segment] is not JsonObject next)
                {
                    next = new JsonObject();
                    cursor[segment] = next;
                }

                cursor = next;
            }

            cursor[entry.JsonPathSegments[^1]] = entry.ValueKind switch
            {
                StructuredParameterValueKind.String => JsonValue.Create(entry.StringValue),
                StructuredParameterValueKind.Boolean => JsonValue.Create(entry.BoolValue),
                StructuredParameterValueKind.Integer => JsonValue.Create(entry.IntegerValue),
                StructuredParameterValueKind.Floating => JsonValue.Create(entry.FloatingValue),
                StructuredParameterValueKind.Null => null,
                _ => JsonValue.Create(entry.StringValue),
            };
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string SerializeToml(IReadOnlyList<StructuredParameterEntry> entries)
        => SerializeIniLike(entries, useTomlSyntax: true);

    private static string SerializeIni(IReadOnlyList<StructuredParameterEntry> entries)
        => SerializeIniLike(entries, useTomlSyntax: false);

    private static string SerializeIniLike(IReadOnlyList<StructuredParameterEntry> entries, bool useTomlSyntax)
    {
        var sb = new StringBuilder();
        var currentSection = string.Empty;

        foreach (var entry in entries)
        {
            var section = entry.Section?.Trim() ?? string.Empty;
            if (!string.Equals(section, currentSection, StringComparison.Ordinal))
            {
                if (sb.Length > 0)
                    sb.AppendLine();

                if (!string.IsNullOrWhiteSpace(section))
                    sb.AppendLine($"[{section}]");

                currentSection = section;
            }

            var value = entry.ValueKind switch
            {
                StructuredParameterValueKind.String => QuoteAndEscape(entry.StringValue),
                StructuredParameterValueKind.Boolean => entry.BoolValue ? "true" : "false",
                StructuredParameterValueKind.Integer => entry.IntegerValue.ToString(CultureInfo.InvariantCulture),
                StructuredParameterValueKind.Floating => entry.FloatingValue.ToString(CultureInfo.InvariantCulture),
                StructuredParameterValueKind.Null => useTomlSyntax ? QuoteAndEscape(string.Empty) : string.Empty,
                _ => QuoteAndEscape(entry.StringValue),
            };

            sb.Append(entry.Key);
            sb.Append(" = ");
            sb.AppendLine(value);
        }

        return sb.ToString();
    }

    private static string BuildSection(IEnumerable<string> path)
        => string.Join('.', path);

    private static string UnwrapQuoted(string value)
    {
        if (value.Length < 2)
            return value;

        var inner = value[1..^1];
        if (value[0] == '"')
            return inner.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        return inner;
    }

    private static string QuoteAndEscape(string value)
        => $"\"{(value ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
