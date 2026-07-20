using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SlurmJobManager.Core.Services;
using SlurmJobManager.App.Services.Updates;

namespace SlurmJobManager.App.Services;

/// <summary>
/// Persists application-level user preferences (e.g. startup auto-connect) to a JSON file
/// under <c>&lt;AppBaseDirectory&gt;/Data/preferences.json</c>.
/// </summary>
public sealed class AppPreferencesService
{
    public const string DefaultRemotePickerDirectoryFallback = "/gpfs/";
    public const int DefaultJobMonitorNotificationDurationSeconds = 8;
    public const int MinJobMonitorNotificationDurationSeconds = 1;
    public const int MaxJobMonitorNotificationDurationSeconds = 3600;

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

    public bool MonitorAllUsersJobs
    {
        get => _dto.MonitorAllUsersJobs;
        set
        {
            if (_dto.MonitorAllUsersJobs == value) return;
            _dto = _dto with { MonitorAllUsersJobs = value };
            Save();
        }
    }

    public bool TrySetMonitorAllUsersJobs(bool value, out string? error)
    {
        if (_dto.MonitorAllUsersJobs != value)
        {
            _dto = _dto with { MonitorAllUsersJobs = value };
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

    public JobMonitorNotificationMode JobMonitorNotificationMode
    {
        get => ParseJobMonitorNotificationMode(_dto.JobMonitorNotificationMode);
        set
        {
            var serialized = value.ToString();
            if (string.Equals(_dto.JobMonitorNotificationMode, serialized, StringComparison.Ordinal))
                return;

            _dto = _dto with { JobMonitorNotificationMode = serialized };
            Save();
        }
    }

    public bool TrySetJobMonitorNotificationMode(JobMonitorNotificationMode value, out string? error)
    {
        var serialized = value.ToString();
        if (!string.Equals(_dto.JobMonitorNotificationMode, serialized, StringComparison.Ordinal))
        {
            _dto = _dto with { JobMonitorNotificationMode = serialized };
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

    public int JobMonitorNotificationDurationSeconds
        => NormalizeJobMonitorNotificationDurationSeconds(_dto.JobMonitorNotificationDurationSeconds);

    public bool TrySetJobMonitorNotificationDurationSeconds(int value, out string? error)
    {
        var normalized = NormalizeJobMonitorNotificationDurationSeconds(value);
        if (_dto.JobMonitorNotificationDurationSeconds != normalized)
        {
            _dto = _dto with { JobMonitorNotificationDurationSeconds = normalized };
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

    public bool JobMonitorNotificationPersistent
    {
        get => _dto.JobMonitorNotificationPersistent;
        set
        {
            if (_dto.JobMonitorNotificationPersistent == value) return;
            _dto = _dto with { JobMonitorNotificationPersistent = value };
            Save();
        }
    }

    public bool TrySetJobMonitorNotificationPersistent(bool value, out string? error)
    {
        if (_dto.JobMonitorNotificationPersistent != value)
        {
            _dto = _dto with { JobMonitorNotificationPersistent = value };
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

    public UpdateSourceType UpdateSourceType
    {
        get => ParseUpdateSourceType(_dto.UpdateSourceType);
        set
        {
            var serialized = value.ToString();
            if (string.Equals(_dto.UpdateSourceType, serialized, StringComparison.Ordinal))
                return;

            _dto = _dto with { UpdateSourceType = serialized };
            Save();
        }
    }

    public bool TrySetUpdateSourceType(UpdateSourceType value, out string? error)
    {
        var serialized = value.ToString();
        if (!string.Equals(_dto.UpdateSourceType, serialized, StringComparison.Ordinal))
        {
            _dto = _dto with { UpdateSourceType = serialized };
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

    public string UpdateFolderPath => NormalizeFolderPath(_dto.UpdateFolderPath);

    public bool TrySetUpdateFolderPath(string? value, out string? error)
    {
        var normalized = NormalizeFolderPath(value);
        if (!string.Equals(_dto.UpdateFolderPath, normalized, StringComparison.Ordinal))
        {
            _dto = _dto with { UpdateFolderPath = normalized };
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

    public bool AutoCheckForUpdatesOnStartup
    {
        get => _dto.AutoCheckForUpdatesOnStartup;
        set
        {
            if (_dto.AutoCheckForUpdatesOnStartup == value) return;
            _dto = _dto with { AutoCheckForUpdatesOnStartup = value };
            Save();
        }
    }

    public bool TrySetAutoCheckForUpdatesOnStartup(bool value, out string? error)
    {
        if (_dto.AutoCheckForUpdatesOnStartup != value)
        {
            _dto = _dto with { AutoCheckForUpdatesOnStartup = value };
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

    public bool IncludePrereleaseUpdates
    {
        get => _dto.IncludePrereleaseUpdates;
        set
        {
            if (_dto.IncludePrereleaseUpdates == value) return;
            _dto = _dto with { IncludePrereleaseUpdates = value };
            Save();
        }
    }

    public bool TrySetIncludePrereleaseUpdates(bool value, out string? error)
    {
        if (_dto.IncludePrereleaseUpdates != value)
        {
            _dto = _dto with { IncludePrereleaseUpdates = value };
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

    public UpdateProxyMode UpdateProxyMode
    {
        get => ParseUpdateProxyMode(_dto.UpdateProxyMode);
        set
        {
            var serialized = value.ToString();
            if (string.Equals(_dto.UpdateProxyMode, serialized, StringComparison.Ordinal))
                return;

            _dto = _dto with { UpdateProxyMode = serialized };
            Save();
        }
    }

    public bool TrySetUpdateProxyMode(UpdateProxyMode value, out string? error)
    {
        var serialized = value.ToString();
        if (!string.Equals(_dto.UpdateProxyMode, serialized, StringComparison.Ordinal))
        {
            _dto = _dto with { UpdateProxyMode = serialized };
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

    public string UpdateCustomProxyHost
        => (_dto.UpdateCustomProxyHost ?? string.Empty).Trim();

    public bool TrySetUpdateCustomProxyHost(string? value, out string? error)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (!string.Equals(_dto.UpdateCustomProxyHost, normalized, StringComparison.Ordinal))
        {
            _dto = _dto with { UpdateCustomProxyHost = normalized };
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

    public int? UpdateCustomProxyPort => _dto.UpdateCustomProxyPort;

    public bool TrySetUpdateCustomProxyPort(int? value, out string? error)
    {
        if (_dto.UpdateCustomProxyPort != value)
        {
            _dto = _dto with { UpdateCustomProxyPort = value };
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

    public bool UseProxyForUpdates
    {
        get => _dto.UseProxyForUpdates;
        set
        {
            if (_dto.UseProxyForUpdates == value) return;
            _dto = _dto with { UseProxyForUpdates = value };
            Save();
        }
    }

    public bool TrySetUseProxyForUpdates(bool value, out string? error)
    {
        if (_dto.UseProxyForUpdates != value)
        {
            _dto = _dto with { UseProxyForUpdates = value };
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
        bool MonitorAllUsersJobs = false,
        string JobMonitorNotificationMode = "Windows",
        int JobMonitorNotificationDurationSeconds = DefaultJobMonitorNotificationDurationSeconds,
        bool JobMonitorNotificationPersistent = false,
        string? DefaultRemotePickerDirectory = DefaultRemotePickerDirectoryFallback,
        string UpdateSourceType = nameof(UpdateSourceType.GitHub),
        string? UpdateFolderPath = "",
        bool AutoCheckForUpdatesOnStartup = false,
        bool IncludePrereleaseUpdates = false,
        string UpdateProxyMode = nameof(UpdateProxyMode.NoProxy),
        string? UpdateCustomProxyHost = "",
        int? UpdateCustomProxyPort = null,
        bool UseProxyForUpdates = false);

    private static string NormalizeRemoteDirectory(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return DefaultRemotePickerDirectoryFallback;

        trimmed = trimmed.Replace('\\', '/');
        if (!trimmed.StartsWith("/", StringComparison.Ordinal))
            trimmed = $"/{trimmed}";

        while (trimmed.Contains("//", StringComparison.Ordinal))
            trimmed = trimmed.Replace("//", "/", StringComparison.Ordinal);

        if (trimmed.Length == 1)
            return "/";

        return $"{trimmed.TrimEnd('/')}/";
    }

    private static string NormalizeFolderPath(string? value)
        => (value ?? string.Empty).Trim();

    private static int NormalizeJobMonitorNotificationDurationSeconds(int value)
        => Math.Clamp(
            value <= 0 ? DefaultJobMonitorNotificationDurationSeconds : value,
            MinJobMonitorNotificationDurationSeconds,
            MaxJobMonitorNotificationDurationSeconds);

    private static UpdateSourceType ParseUpdateSourceType(string? value)
        => Enum.TryParse<UpdateSourceType>(value, ignoreCase: true, out var parsed)
            ? parsed
            : UpdateSourceType.GitHub;

    private static UpdateProxyMode ParseUpdateProxyMode(string? value)
        => Enum.TryParse<UpdateProxyMode>(value, ignoreCase: true, out var parsed)
            ? parsed
            : UpdateProxyMode.NoProxy;

    private static JobMonitorNotificationMode ParseJobMonitorNotificationMode(string? value)
        => Enum.TryParse<JobMonitorNotificationMode>(value, ignoreCase: true, out var parsed)
            ? parsed
            : JobMonitorNotificationMode.Windows;
}
