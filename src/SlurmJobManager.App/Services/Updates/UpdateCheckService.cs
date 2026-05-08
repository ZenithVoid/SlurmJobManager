using System.IO;
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
    private readonly IApplicationVersionService _versionService;
    private readonly IAppLogger? _logger;
    private readonly HttpClient _httpClient;

    public UpdateCheckService(IApplicationVersionService versionService, IAppLogger? logger = null)
    {
        _versionService = versionService ?? throw new ArgumentNullException(nameof(versionService));
        _logger = logger;
        _httpClient = BuildHttpClient(_versionService.CurrentVersion);
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(UpdateCheckRequest request, CancellationToken cancellationToken = default)
    {
        _logger?.Info($"UpdateCheckService started. Source={request.SourceType}, IncludePrerelease={request.IncludePrerelease}");
        return request.SourceType switch
        {
            UpdateSourceType.GitHub => await CheckGitHubAsync(request.IncludePrerelease, cancellationToken),
            UpdateSourceType.Folder => await CheckFolderAsync(request.FolderPath),
            _ => BuildFailure(UpdateSourceType.GitHub, "Unsupported update source type."),
        };
    }

    private async Task<UpdateCheckResult> CheckGitHubAsync(bool includePrerelease, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(GitHubReleasesApi, cancellationToken);
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
                if (!includePrerelease && isPrerelease)
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
                includePrerelease
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

            var versionFilePath = Path.Combine(path, "version.json");
            if (!File.Exists(versionFilePath))
                return Task.FromResult(BuildFailure(UpdateSourceType.Folder, "version.json was not found in the update folder.", path));

            var json = File.ReadAllText(versionFilePath);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var versionElement))
                return Task.FromResult(BuildFailure(UpdateSourceType.Folder, "version.json is missing required field: version.", path));

            var versionText = versionElement.GetString();
            if (!VersionTextParser.TryParse(versionText, out var remoteVersion))
                return Task.FromResult(BuildFailure(UpdateSourceType.Folder, "version.json contains an invalid version.", path));

            var title = TryGetString(root, "title");
            var notes = TryGetString(root, "notes");
            var publishedAtRaw = TryGetString(root, "publishedAt");
            _ = DateTimeOffset.TryParse(publishedAtRaw, out var publishedAt);

            var package = TryGetString(root, "package");
            var target = ResolveFolderTarget(path, package, remoteVersion);

            _logger?.Info($"Folder update check resolved version '{versionText}' from '{path}'.");
            return Task.FromResult(BuildSuccess(
                UpdateSourceType.Folder,
                remoteVersion,
                versionText,
                title,
                publishedAt,
                notes,
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

    private static HttpClient BuildHttpClient(Version currentVersion)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SlurmJobManager", currentVersion.ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
