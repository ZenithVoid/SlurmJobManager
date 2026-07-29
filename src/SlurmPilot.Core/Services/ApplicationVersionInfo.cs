using System.Reflection;

namespace SlurmPilot.Core.Services;

public sealed record ApplicationVersionInfo(
    Version ComparableVersion,
    string DisplayVersion,
    string? InformationalVersion)
{
    private static readonly Version DefaultVersion = new(1, 0, 0, 0);

    public static ApplicationVersionInfo Resolve(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var fallback = assembly.GetName().Version ?? DefaultVersion;
        var parsed = VersionTextParser.TryParse(informational, out var infoVersion)
            ? infoVersion
            : fallback;

        return new ApplicationVersionInfo(
            ComparableVersion: parsed,
            DisplayVersion: NormalizeDisplayVersion(informational, parsed),
            InformationalVersion: informational?.Trim());
    }

    private static string NormalizeDisplayVersion(string? informational, Version parsed)
    {
        if (string.IsNullOrWhiteSpace(informational))
            return FormatComparableVersion(parsed);

        var trimmed = informational.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];

        var metadataIndex = trimmed.IndexOf('+');
        if (metadataIndex >= 0)
            trimmed = trimmed[..metadataIndex];

        return string.IsNullOrWhiteSpace(trimmed)
            ? FormatComparableVersion(parsed)
            : trimmed;
    }

    private static string FormatComparableVersion(Version version)
        => version.Revision > 0 ? version.ToString(4) : version.ToString(3);
}

public static class VersionTextParser
{
    public static bool TryParse(string? input, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];

        var digits = new List<int>();
        var part = string.Empty;

        foreach (var ch in trimmed)
        {
            if (char.IsDigit(ch))
            {
                part += ch;
                continue;
            }

            if (ch == '.')
            {
                if (!TryAppendPart(part, digits))
                    return false;
                part = string.Empty;
                continue;
            }

            break;
        }

        if (!TryAppendPart(part, digits) || digits.Count == 0)
            return false;

        while (digits.Count < 4)
            digits.Add(0);

        version = new Version(digits[0], digits[1], digits[2], digits[3]);
        return true;
    }

    private static bool TryAppendPart(string part, List<int> target)
    {
        if (string.IsNullOrWhiteSpace(part))
            return target.Count > 0;

        if (!int.TryParse(part, out var parsed) || parsed < 0)
            return false;

        if (target.Count >= 4)
            return true;

        target.Add(parsed);
        return true;
    }
}
