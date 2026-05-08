using System.Reflection;

namespace SlurmJobManager.App.Services.Updates;

public sealed class ApplicationVersionService : IApplicationVersionService
{
    private static readonly Version DefaultVersion = new(1, 0, 0, 0);

    public Version CurrentVersion { get; }
    public string CurrentVersionDisplay { get; }

    public ApplicationVersionService()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var fallback = assembly.GetName().Version;
        var parsed = UpdateVersionParser.TryParse(informational, out var infoVersion)
            ? infoVersion
            : fallback ?? DefaultVersion;

        CurrentVersion = parsed;
        CurrentVersionDisplay = informational?.Trim() is { Length: > 0 } nonEmpty
            ? nonEmpty
            : parsed.ToString();
    }
}

internal static class UpdateVersionParser
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
