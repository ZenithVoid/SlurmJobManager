using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SlurmJobManager.Core.Services;

namespace SlurmJobManager.App.Services;

/// <summary>
/// Persists application-level user preferences (e.g. startup auto-connect) to a JSON file
/// under <c>&lt;AppBaseDirectory&gt;/Data/preferences.json</c>.
/// </summary>
public sealed class AppPreferencesService
{
    private static readonly string PrefsPath = LocalDataPaths.PreferencesFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private AppPrefsDto _dto;
    public bool LastSaveSucceeded { get; private set; } = true;
    public string? LastSaveError { get; private set; }

    public AppPreferencesService()
    {
        _dto = Load();
    }

    /// <summary>
    /// When <c>true</c> the app will automatically invoke ConnectCommand after loading a saved
    /// profile on startup.
    /// </summary>
    public bool AutoConnectOnStartup
    {
        get => _dto.AutoConnectOnStartup;
        set
        {
            if (_dto.AutoConnectOnStartup == value) return;
            _dto = _dto with { AutoConnectOnStartup = value };
            Save();
        }
    }

    public bool TrySetAutoConnectOnStartup(bool value, out string? error)
    {
        if (_dto.AutoConnectOnStartup != value)
        {
            _dto = _dto with { AutoConnectOnStartup = value };
            Save();
        }
        else
        {
            LastSaveSucceeded = true;
            LastSaveError = null;
        }

        error = LastSaveError;
        return LastSaveSucceeded;
    }

    /// <summary>
    /// When <c>true</c> the app restores the last task context for the connected host/user
    /// and automatically navigates to the task page when a valid task ID exists.
    /// </summary>
    public bool AutoRestoreLastTaskOnLogin
    {
        get => _dto.AutoRestoreLastTaskOnLogin;
        set
        {
            if (_dto.AutoRestoreLastTaskOnLogin == value) return;
            _dto = _dto with { AutoRestoreLastTaskOnLogin = value };
            Save();
        }
    }

    public bool TrySetAutoRestoreLastTaskOnLogin(bool value, out string? error)
    {
        if (_dto.AutoRestoreLastTaskOnLogin != value)
        {
            _dto = _dto with { AutoRestoreLastTaskOnLogin = value };
            Save();
        }
        else
        {
            LastSaveSucceeded = true;
            LastSaveError = null;
        }

        error = LastSaveError;
        return LastSaveSucceeded;
    }

    /// <summary>
    /// Default starting directory for remote SSH file/directory picker dialogs.
    /// Falls back to <c>/gpfs/</c> when not configured.
    /// </summary>
    public string DefaultRemotePickerDirectory
        => NormalizeRemoteDirectory(_dto.DefaultRemotePickerDirectory);

    public bool TrySetDefaultRemotePickerDirectory(string? value, out string? error)
    {
        var normalized = NormalizeRemoteDirectory(value);
        if (!string.Equals(NormalizeRemoteDirectory(_dto.DefaultRemotePickerDirectory), normalized, StringComparison.Ordinal))
        {
            _dto = _dto with { DefaultRemotePickerDirectory = normalized };
            Save();
        }
        else
        {
            LastSaveSucceeded = true;
            LastSaveError = null;
        }

        error = LastSaveError;
        return LastSaveSucceeded;
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    private AppPrefsDto Load()
    {
        try
        {
            if (File.Exists(PrefsPath))
            {
                var json = File.ReadAllText(PrefsPath);
                return JsonSerializer.Deserialize<AppPrefsDto>(json, JsonOptions) ?? new AppPrefsDto();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppPreferencesService.Load] {ex.Message}");
        }
        return new AppPrefsDto();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
            File.WriteAllText(PrefsPath, JsonSerializer.Serialize(_dto, JsonOptions));
            LastSaveSucceeded = true;
            LastSaveError = null;
        }
        catch (Exception ex)
        {
            LastSaveSucceeded = false;
            LastSaveError = ex.Message;
            System.Diagnostics.Debug.WriteLine($"[AppPreferencesService.Save] {ex.Message}");
        }
    }

    // ── DTO ──────────────────────────────────────────────────────────────────

    private sealed record AppPrefsDto(
        bool AutoConnectOnStartup = false,
        bool AutoRestoreLastTaskOnLogin = false,
        string? DefaultRemotePickerDirectory = "/gpfs/");

    private static string NormalizeRemoteDirectory(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "/gpfs/";

        trimmed = trimmed.Replace('\\', '/');
        if (!trimmed.StartsWith("/", StringComparison.Ordinal))
            trimmed = $"/{trimmed}";

        while (trimmed.Contains("//", StringComparison.Ordinal))
            trimmed = trimmed.Replace("//", "/", StringComparison.Ordinal);

        if (trimmed.Length == 1)
            return "/";

        return $"{trimmed.TrimEnd('/')}/";
    }
}
