namespace SlurmJobManager.App.Services;

public static class RemotePathDisplayHelper
{
    public static string NormalizeRemotePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        if (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
            normalized = normalized[..^1];
        return normalized;
    }

    public static string ExpandHomePath(string? path, string? homeDirectory)
    {
        var normalizedInput = NormalizeRemotePath(path);
        if (string.IsNullOrWhiteSpace(normalizedInput)) return string.Empty;

        var normalizedHome = NormalizeRemotePath(homeDirectory);
        if (string.IsNullOrWhiteSpace(normalizedHome)) return normalizedInput;

        if (normalizedInput == "~") return normalizedHome;
        if (normalizedInput.StartsWith("~/", StringComparison.Ordinal))
            return NormalizeRemotePath($"{normalizedHome}/{normalizedInput[2..]}");

        return normalizedInput;
    }

    public static string CollapseHomePath(string? path, string? homeDirectory)
    {
        var normalizedPath = NormalizeRemotePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath)) return string.Empty;

        var normalizedHome = NormalizeRemotePath(homeDirectory);
        if (string.IsNullOrWhiteSpace(normalizedHome)) return normalizedPath;

        if (string.Equals(normalizedPath, normalizedHome, StringComparison.Ordinal))
            return "~";

        if (normalizedPath.StartsWith($"{normalizedHome}/", StringComparison.Ordinal))
            return $"~/{normalizedPath[(normalizedHome.Length + 1)..]}";

        return normalizedPath;
    }
}
