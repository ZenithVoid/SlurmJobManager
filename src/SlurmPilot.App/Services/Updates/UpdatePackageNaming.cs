using System.IO;
using SlurmPilot.Core.Services;

namespace SlurmPilot.App.Services.Updates;

internal static class UpdatePackageNaming
{
    private const string ProductPrefix = "SlurmPilot-";
    private static readonly string[] SupportedPackageExtensions = [".zip", ".exe", ".msi"];

    public static bool IsSupportedPackage(string path)
    {
        var extension = Path.GetExtension(path);
        return SupportedPackageExtensions.Any(ext => string.Equals(ext, extension, StringComparison.OrdinalIgnoreCase));
    }

    public static string? ResolveBestPackagePath(string directoryPath, Version? preferredVersion, out bool hasMultipleCandidates)
    {
        hasMultipleCandidates = false;
        if (!Directory.Exists(directoryPath))
            return null;

        var candidates = Directory
            .EnumerateFiles(directoryPath)
            .Where(IsSupportedPackage)
            .ToList();

        hasMultipleCandidates = candidates.Count > 1;
        if (candidates.Count == 0)
            return null;

        var preferredV3 = preferredVersion?.ToString(3);
        var preferredV4 = preferredVersion?.ToString(4);

        var ordered = candidates
            .Select(path =>
            {
                var fileName = Path.GetFileName(path);
                var startsWithProductPrefix = fileName.StartsWith(ProductPrefix, StringComparison.OrdinalIgnoreCase);
                var hasParsedVersion = TryParseVersionFromPackageName(path, out var packageVersion);
                var versionMatchesPreferred = preferredVersion is not null && hasParsedVersion && packageVersion == preferredVersion;
                var containsPreferredText = preferredV3 is not null && fileName.Contains(preferredV3, StringComparison.OrdinalIgnoreCase)
                                            || preferredV4 is not null && fileName.Contains(preferredV4, StringComparison.OrdinalIgnoreCase);
                var score =
                    (versionMatchesPreferred ? 10_000 : 0) +
                    (containsPreferredText ? 4_000 : 0) +
                    (startsWithProductPrefix ? 500 : 0) +
                    (hasParsedVersion ? 300 : 0) +
                    (fileName.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase) ? 50 : 0);

                return new
                {
                    Path = path,
                    Score = score,
                    ParsedVersion = hasParsedVersion ? packageVersion : new Version(0, 0, 0, 0),
                    LastWriteTimeUtc = File.GetLastWriteTimeUtc(path),
                    FileName = fileName,
                };
            })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.ParsedVersion)
            .ThenByDescending(x => x.LastWriteTimeUtc)
            .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ordered[0].Path;
    }

    public static bool TryParseVersionFromPackageName(string packagePath, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        var fileName = Path.GetFileNameWithoutExtension(packagePath);
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        if (!fileName.StartsWith(ProductPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = fileName[ProductPrefix.Length..];
        var firstToken = suffix.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return VersionTextParser.TryParse(firstToken, out version);
    }
}
