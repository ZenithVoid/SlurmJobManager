using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Services;

namespace SlurmJobManager.App.Services.Updates;

public sealed class UpdateCheckService : IUpdateCheckService
{
    private const string GitHubOwner = "ZenithVoid";
    private const string GitHubRepo = "SlurmJobManager";
    private const string GitHubReleasesApi = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases?per_page=20";
    private const string GitHubReleasesPage = $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases";
    private const string LatestManifestFileName = "latest.json";
    private const string LegacyManifestFileName = "version.json";
    private readonly IApplicationVersionService _versionService;
    private readonly IAppLogger? _logger;

    public UpdateCheckService(IApplicationVersionService versionService, IAppLogger? logger = null)
    {
        _versionService = versionService ?? throw new ArgumentNullException(nameof(versionService));
        _logger = logger;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(UpdateCheckRequest request, CancellationToken cancellationToken = default)
    {
        _logger?.Info(
            $"UpdateCheckService started. Source={request.SourceType}, IncludePrerelease={request.IncludePrerelease}, ProxyEnabled={request.UseProxyForUpdates}, ProxyMode={request.ProxyMode}");
        return request.SourceType switch
        {
            UpdateSourceType.GitHub => await CheckGitHubAsync(request, cancellationToken),
            UpdateSourceType.Folder => await CheckFolderAsync(request.FolderPath),
            _ => BuildFailure(UpdateSourceType.GitHub, "Unsupported update source type."),
        };
    }

    private async Task<UpdateCheckResult> CheckGitHubAsync(UpdateCheckRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var client = BuildHttpClient(_versionService.CurrentVersion, request, out var proxyLog);
            _logger?.Info($"GitHub update check proxy policy: {proxyLog}");

            using var response = await client.GetAsync(GitHubReleasesApi, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.Warning($"GitHub update check returned HTTP {(int)response.StatusCode}.");
                return BuildFailure(
                    UpdateSourceType.GitHub,
                    $"GitHub update check failed with HTTP {(int)response.StatusCode}.",
                    GitHubReleasesPage);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return BuildFailure(UpdateSourceType.GitHub, "GitHub API returned an unexpected response format.", GitHubReleasesPage);

            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draftElement) && draftElement.ValueKind == JsonValueKind.True)
                    continue;

                var isPrerelease = release.TryGetProperty("prerelease", out var prereleaseElement) && prereleaseElement.ValueKind == JsonValueKind.True;
                if (!request.IncludePrerelease && isPrerelease)
                    continue;

                var tag = TryGetString(release, "tag_name");
                if (!VersionTextParser.TryParse(tag, out var remoteVersion))
                    continue;

                var releaseName = TryGetString(release, "name");
                var publishedAtRaw = TryGetString(release, "published_at");
                var notes = TryGetString(release, "body");
                var releaseUrl = TryGetString(release, "html_url") ?? GitHubReleasesPage;
                _ = DateTimeOffset.TryParse(publishedAtRaw, out var publishedAt);

                _logger?.Info($"GitHub update check resolved version '{tag}'.");
                return BuildSuccess(
                    UpdateSourceType.GitHub,
                    remoteVersion,
                    tag,
                    releaseName,
                    publishedAt,
                    notes,
                    releaseUrl);
            }

            return BuildFailure(
                UpdateSourceType.GitHub,
                request.IncludePrerelease
                    ? "No parseable GitHub release version was found."
                    : "No stable GitHub release was found.",
                GitHubReleasesPage);
        }
        catch (OperationCanceledException)
        {
            _logger?.Warning("GitHub update check was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Error("GitHub update check failed.", ex);
            return BuildFailure(UpdateSourceType.GitHub, ex.Message, GitHubReleasesPage);
        }
    }

    private Task<UpdateCheckResult> CheckFolderAsync(string? folderPath)
    {
        try
        {
            var path = (folderPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(path))
                return Task.FromResult(BuildFailure(UpdateSourceType.Folder, "Update folder path is empty."));

            if (!Directory.Exists(path))
                return Task.FromResult(BuildFailure(UpdateSourceType.Folder, "Update folder does not exist.", path));

            if (!TryLoadFolderManifest(path, out var manifest, out var manifestError))
                return Task.FromResult(BuildFailure(UpdateSourceType.Folder, manifestError ?? "Failed to load update manifest.", path));

            var target = ResolveFolderTarget(path, manifest!.Package, manifest.RemoteVersion);

            _logger?.Info($"Folder update check resolved version '{manifest.VersionText}' from '{path}'.");
            return Task.FromResult(BuildSuccess(
                UpdateSourceType.Folder,
                manifest.RemoteVersion,
                manifest.VersionText,
                manifest.Title,
                manifest.PublishedAt ?? default,
                manifest.Notes,
                target));
        }
        catch (Exception ex)
        {
            _logger?.Error($"Folder update check failed for path '{folderPath}'.", ex);
            return Task.FromResult(BuildFailure(UpdateSourceType.Folder, ex.Message, folderPath));
        }
    }

    private static string? ResolveFolderTarget(string folderPath, string? package, Version remoteVersion)
    {
        if (!string.IsNullOrWhiteSpace(package))
        {
            var combined = Path.IsPathRooted(package)
                ? package
                : Path.Combine(folderPath, package);
            if (File.Exists(combined))
                return combined;
        }

        var inferredPackage = UpdatePackageNaming.ResolveBestPackagePath(folderPath, remoteVersion, out _);
        if (!string.IsNullOrWhiteSpace(inferredPackage))
            return inferredPackage;

        return folderPath;
    }

    private static bool TryLoadFolderManifest(string folderPath, out FolderManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;

        var latestPath = Path.Combine(folderPath, LatestManifestFileName);
        if (File.Exists(latestPath))
            return TryParseManifestFile(latestPath, isLatestFormat: true, out manifest, out error);

        var legacyPath = Path.Combine(folderPath, LegacyManifestFileName);
        if (File.Exists(legacyPath))
            return TryParseManifestFile(legacyPath, isLatestFormat: false, out manifest, out error);

        error = $"{LatestManifestFileName} or {LegacyManifestFileName} was not found in the update folder.";
        return false;
    }

    private static bool TryParseManifestFile(string manifestPath, bool isLatestFormat, out FolderManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;

        try
        {
            var json = File.ReadAllText(manifestPath);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var fileName = Path.GetFileName(manifestPath);

            var versionText = TryGetString(root, "version");
            if (string.IsNullOrWhiteSpace(versionText))
            {
                error = $"{fileName} is missing required field: version.";
                return false;
            }

            if (!VersionTextParser.TryParse(versionText, out var remoteVersion))
            {
                error = $"{fileName} contains an invalid version.";
                return false;
            }

            var title = TryGetString(root, "title");
            var notes = TryGetString(root, "notes");
            var publishedAtRaw = TryGetString(root, "publishedAt");
            DateTimeOffset? publishedAt = DateTimeOffset.TryParse(publishedAtRaw, out var parsedPublishedAt)
                ? parsedPublishedAt
                : null;
            var package = isLatestFormat
                ? ResolveLatestManifestPackage(root)
                : TryGetString(root, "package");

            manifest = new FolderManifest(versionText, remoteVersion, title, notes, publishedAt, package);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to parse manifest '{Path.GetFileName(manifestPath)}': {ex.Message}";
            return false;
        }
    }

    private static string? ResolveLatestManifestPackage(JsonElement root)
    {
        var direct = TryGetString(root, "package")
                     ?? TryGetString(root, "relativePath")
                     ?? TryGetString(root, "fileName");
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        if (!root.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
            return null;

        string? fallback = null;
        foreach (var package in packages.EnumerateArray())
        {
            if (package.ValueKind != JsonValueKind.Object)
                continue;

            var candidate = TryGetString(package, "relativePath")
                            ?? TryGetString(package, "fileName")
                            ?? TryGetString(package, "package");
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var packageType = TryGetString(package, "packageType");
            if (string.Equals(packageType, "zip", StringComparison.OrdinalIgnoreCase))
                return candidate;

            fallback ??= candidate;
        }

        return fallback;
    }

    private UpdateCheckResult BuildSuccess(
        UpdateSourceType sourceType,
        Version latestVersion,
        string? latestVersionDisplay,
        string? releaseTitle,
        DateTimeOffset publishedAt,
        string? releaseNotes,
        string? openTarget)
    {
        return new UpdateCheckResult(
            IsSuccess: true,
            SourceType: sourceType,
            CurrentVersion: _versionService.CurrentVersion,
            CurrentVersionDisplay: _versionService.CurrentVersionDisplay,
            LatestVersion: latestVersion,
            LatestVersionDisplay: !string.IsNullOrWhiteSpace(latestVersionDisplay) ? latestVersionDisplay : latestVersion.ToString(),
            ReleaseTitle: releaseTitle,
            PublishedAt: publishedAt == default ? null : publishedAt,
            ReleaseNotes: releaseNotes,
            OpenTarget: openTarget,
            ErrorMessage: null);
    }

    private UpdateCheckResult BuildFailure(UpdateSourceType sourceType, string errorMessage, string? openTarget = null)
    {
        return new UpdateCheckResult(
            IsSuccess: false,
            SourceType: sourceType,
            CurrentVersion: _versionService.CurrentVersion,
            CurrentVersionDisplay: _versionService.CurrentVersionDisplay,
            LatestVersion: null,
            LatestVersionDisplay: null,
            ReleaseTitle: null,
            PublishedAt: null,
            ReleaseNotes: null,
            OpenTarget: openTarget,
            ErrorMessage: errorMessage);
    }

    private static string? TryGetString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private sealed record FolderManifest(
        string VersionText,
        Version RemoteVersion,
        string? Title,
        string? Notes,
        DateTimeOffset? PublishedAt,
        string? Package);

    private static HttpClient BuildHttpClient(Version currentVersion, UpdateCheckRequest request, out string proxyLog)
    {
        var handler = new HttpClientHandler();
        proxyLog = "disabled";

        if (!request.UseProxyForUpdates)
        {
            handler.UseProxy = false;
            handler.Proxy = null;
            proxyLog = "disabled-by-setting";
        }
        else
        {
            switch (request.ProxyMode)
            {
                case UpdateProxyMode.NoProxy:
                    handler.UseProxy = false;
                    handler.Proxy = null;
                    proxyLog = "no-proxy (system proxy ignored)";
                    break;
                case UpdateProxyMode.SystemProxy:
                    handler.UseProxy = true;
                    handler.Proxy = null;
                    proxyLog = "system-proxy";
                    break;
                case UpdateProxyMode.CustomProxy:
                    if (!UpdateProxyValidation.TryBuildCustomProxyUri(
                            request.CustomProxyHost,
                            request.CustomProxyPort,
                            out var proxyUri,
                            out var validationError))
                    {
                        throw new InvalidOperationException(validationError ?? "Custom proxy configuration is invalid.");
                    }

                    handler.UseProxy = true;
                    handler.Proxy = new WebProxy(proxyUri!);
                    proxyLog = $"custom-proxy ({proxyUri!.Host}:{proxyUri.Port})";
                    break;
                default:
                    handler.UseProxy = false;
                    handler.Proxy = null;
                    proxyLog = "disabled-by-unknown-mode";
                    break;
            }
        }

        var client = new HttpClient(handler, disposeHandler: true);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SlurmJobManager", currentVersion.ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
