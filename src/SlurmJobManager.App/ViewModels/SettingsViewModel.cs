using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using SlurmJobManager.App.Services;
using SlurmJobManager.App.Services.Updates;
using SlurmJobManager.Core.Services;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// Settings page: wraps the connection configuration and exposes app-wide preferences
/// (theme, polling frequency, etc.) through the main view-model.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private const string GitHubReleasesPage = "https://github.com/ZenithVoid/SlurmJobManager/releases";

    private readonly MainViewModel _main;
    private readonly AppPreferencesService _prefs;
    private readonly IUpdateCheckService _updateCheckService;
    private readonly IApplicationVersionService _versionService;

    private string _updateStatusMessage;
    private string _latestVersionDisplay = "-";
    private string _lastCheckedAtDisplay = "-";
    private string _updateSourceDisplay;
    private string _releaseTitle = "-";
    private string _releasePublishedAt = "-";
    private string _releaseNotes = string.Empty;
    private bool _hasUpdate;
    private bool _isCheckingUpdates;
    private bool _hasCheckedUpdates;
    private string? _lastOpenTarget;

    public SettingsViewModel(
        MainViewModel main,
        AppPreferencesService prefs,
        IUpdateCheckService updateCheckService,
        IApplicationVersionService versionService)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _prefs = prefs ?? throw new ArgumentNullException(nameof(prefs));
        _updateCheckService = updateCheckService ?? throw new ArgumentNullException(nameof(updateCheckService));
        _versionService = versionService ?? throw new ArgumentNullException(nameof(versionService));

        _updateStatusMessage = L("Settings.UpdateNotCheckedYet");
        _updateSourceDisplay = L("Settings.UpdateSourceUnknown");

        CheckForUpdatesCommand = new AsyncRelayCommand(() => CheckForUpdatesAsync(showToasts: true), () => !IsCheckingUpdates);
        OpenUpdateTargetCommand = new RelayCommand(OpenUpdateTarget, CanOpenUpdateTarget);
        OpenUpdateSourceCommand = new RelayCommand(OpenConfiguredUpdateSource);
    }

    // ── Connection configuration (re-exposed for embedding ConnectionView) ──

    public ConnectionViewModel Connection => _main.Connection;

    // ── Startup auto-connect ──────────────────────────────────────────────

    public bool AutoConnectOnStartup
    {
        get => _prefs.AutoConnectOnStartup;
        set
        {
            if (_prefs.TrySetAutoConnectOnStartup(value, out var saveError))
                ToastService.Instance.Success(L("Settings.AutoSaveSuccess"));
            else
                ToastService.Instance.Error(string.Format(L("Settings.AutoSaveFailedFormat"), saveError ?? L("Settings.UnknownError")));
            OnPropertyChanged();
        }
    }

    public bool AutoRestoreLastTaskOnLogin
    {
        get => _prefs.AutoRestoreLastTaskOnLogin;
        set
        {
            if (_prefs.TrySetAutoRestoreLastTaskOnLogin(value, out var saveError))
                ToastService.Instance.Success(L("Settings.AutoSaveSuccess"));
            else
                ToastService.Instance.Error(string.Format(L("Settings.AutoSaveFailedFormat"), saveError ?? L("Settings.UnknownError")));
            OnPropertyChanged();
        }
    }

    public string DefaultRemotePickerDirectory
    {
        get => _prefs.DefaultRemotePickerDirectory;
        set
        {
            if (_prefs.TrySetDefaultRemotePickerDirectory(value, out var saveError))
                ToastService.Instance.Success(L("Settings.AutoSaveSuccess"));
            else
                ToastService.Instance.Error(string.Format(L("Settings.AutoSaveFailedFormat"), saveError ?? L("Settings.UnknownError")));

            OnPropertyChanged();
        }
    }

    // ── Theme ─────────────────────────────────────────────────────────────

    public bool IsDarkTheme
    {
        get => _main.IsDarkTheme;
        set => _main.IsDarkTheme = value;
    }

    public string ThemeLabel => _main.IsDarkTheme ? L("Settings.ThemeLight") : L("Settings.ThemeDark");
    public ICommand ToggleThemeCommand => _main.ToggleThemeCommand;

    // ── Polling settings (delegated to MonitorViewModel) ─────────────────

    public int PollIntervalSeconds
    {
        get => _main.Monitor.PollIntervalSeconds;
        set
        {
            _main.Monitor.PollIntervalSeconds = value;
            OnPropertyChanged();
        }
    }

    // ── Locale ────────────────────────────────────────────────────────────

    public string CurrentLocale => _main.CurrentLocale;
    public string CurrentLocaleDisplayName => CurrentLocale == "en-US" ? L("Settings.LangEnUS") : L("Settings.LangZhCN");
    public ICommand SwitchLocaleCommand => _main.SwitchLocaleCommand;
    public ICommand OpenDataDirectoryCommand => new RelayCommand(OpenDataDirectory);
    public ICommand CopyDataDirectoryCommand => new RelayCommand(CopyDataDirectory);

    // ── Local data paths ───────────────────────────────────────────────────

    public string LocalDataDirectory => LocalDataPaths.DataDirectory;
    public string TasksDirectory => LocalDataPaths.TasksDirectory;
    public string BlueprintsDirectory => LocalDataPaths.BlueprintsDirectory;
    public string RecentConnectionsFilePath => LocalDataPaths.RecentConnectionsFilePath;
    public string PreferencesFilePath => LocalDataPaths.PreferencesFilePath;
    public string LastTaskContextsFilePath => LocalDataPaths.LastTaskContextsFilePath;

    // ── Update check ───────────────────────────────────────────────────────

    public string CurrentAppVersion => _versionService.CurrentVersionDisplay;

    public int SelectedUpdateSourceIndex
    {
        get => _prefs.UpdateSourceType == UpdateSourceType.Folder ? 1 : 0;
        set
        {
            var newSource = value == 1 ? UpdateSourceType.Folder : UpdateSourceType.GitHub;
            if (_prefs.TrySetUpdateSourceType(newSource, out var saveError))
                ToastService.Instance.Success(L("Settings.AutoSaveSuccess"));
            else
                ToastService.Instance.Error(string.Format(L("Settings.AutoSaveFailedFormat"), saveError ?? L("Settings.UnknownError")));

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFolderUpdateSource));
            OnPropertyChanged(nameof(IsGitHubUpdateSource));
        }
    }

    public bool IsFolderUpdateSource => _prefs.UpdateSourceType == UpdateSourceType.Folder;
    public bool IsGitHubUpdateSource => _prefs.UpdateSourceType == UpdateSourceType.GitHub;

    public string UpdateFolderPath
    {
        get => _prefs.UpdateFolderPath;
        set
        {
            if (_prefs.TrySetUpdateFolderPath(value, out var saveError))
                ToastService.Instance.Success(L("Settings.AutoSaveSuccess"));
            else
                ToastService.Instance.Error(string.Format(L("Settings.AutoSaveFailedFormat"), saveError ?? L("Settings.UnknownError")));
            OnPropertyChanged();
        }
    }

    public bool AutoCheckForUpdatesOnStartup
    {
        get => _prefs.AutoCheckForUpdatesOnStartup;
        set
        {
            if (_prefs.TrySetAutoCheckForUpdatesOnStartup(value, out var saveError))
                ToastService.Instance.Success(L("Settings.AutoSaveSuccess"));
            else
                ToastService.Instance.Error(string.Format(L("Settings.AutoSaveFailedFormat"), saveError ?? L("Settings.UnknownError")));
            OnPropertyChanged();
        }
    }

    public bool IncludePrereleaseUpdates
    {
        get => _prefs.IncludePrereleaseUpdates;
        set
        {
            if (_prefs.TrySetIncludePrereleaseUpdates(value, out var saveError))
                ToastService.Instance.Success(L("Settings.AutoSaveSuccess"));
            else
                ToastService.Instance.Error(string.Format(L("Settings.AutoSaveFailedFormat"), saveError ?? L("Settings.UnknownError")));
            OnPropertyChanged();
        }
    }

    public bool IsCheckingUpdates
    {
        get => _isCheckingUpdates;
        private set => SetField(ref _isCheckingUpdates, value);
    }

    public bool HasUpdate
    {
        get => _hasUpdate;
        private set
        {
            if (!SetField(ref _hasUpdate, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string UpdateStatusMessage
    {
        get => _updateStatusMessage;
        private set => SetField(ref _updateStatusMessage, value);
    }

    public string LatestVersionDisplay
    {
        get => _latestVersionDisplay;
        private set => SetField(ref _latestVersionDisplay, value);
    }

    public string LastCheckedAtDisplay
    {
        get => _lastCheckedAtDisplay;
        private set => SetField(ref _lastCheckedAtDisplay, value);
    }

    public string UpdateSourceDisplay
    {
        get => _updateSourceDisplay;
        private set => SetField(ref _updateSourceDisplay, value);
    }

    public string ReleaseTitle
    {
        get => _releaseTitle;
        private set => SetField(ref _releaseTitle, value);
    }

    public string ReleasePublishedAt
    {
        get => _releasePublishedAt;
        private set => SetField(ref _releasePublishedAt, value);
    }

    public string ReleaseNotes
    {
        get => _releaseNotes;
        private set => SetField(ref _releaseNotes, value);
    }

    public ICommand CheckForUpdatesCommand { get; }
    public ICommand OpenUpdateTargetCommand { get; }
    public ICommand OpenUpdateSourceCommand { get; }

    public async Task TryAutoCheckUpdatesOnStartupAsync()
    {
        if (!AutoCheckForUpdatesOnStartup)
            return;

        await CheckForUpdatesAsync(showToasts: false);
    }

    internal void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(ThemeLabel));
    }

    internal void NotifyLocaleChanged()
    {
        OnPropertyChanged(nameof(CurrentLocale));
        OnPropertyChanged(nameof(CurrentLocaleDisplayName));
        OnPropertyChanged(nameof(ThemeLabel));
        if (!_hasCheckedUpdates)
            UpdateStatusMessage = L("Settings.UpdateNotCheckedYet");
    }

    private async Task CheckForUpdatesAsync(bool showToasts)
    {
        try
        {
            IsCheckingUpdates = true;
            UpdateStatusMessage = L("Settings.UpdateChecking");
            CommandManager.InvalidateRequerySuggested();

            var result = await _updateCheckService.CheckForUpdatesAsync(new UpdateCheckRequest(
                _prefs.UpdateSourceType,
                _prefs.IncludePrereleaseUpdates,
                _prefs.UpdateFolderPath));

            ApplyUpdateResult(result, showToasts);
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = string.Format(L("Settings.UpdateCheckFailedFormat"), ex.Message);
            _hasCheckedUpdates = true;
            if (showToasts)
                ToastService.Instance.Error(UpdateStatusMessage);
        }
        finally
        {
            IsCheckingUpdates = false;
            LastCheckedAtDisplay = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ApplyUpdateResult(UpdateCheckResult result, bool showToasts)
    {
        UpdateSourceDisplay = result.SourceType == UpdateSourceType.GitHub
            ? L("Settings.UpdateSourceGitHub")
            : L("Settings.UpdateSourceFolder");
        LatestVersionDisplay = result.LatestVersionDisplay ?? "-";
        ReleaseTitle = string.IsNullOrWhiteSpace(result.ReleaseTitle) ? "-" : result.ReleaseTitle;
        ReleasePublishedAt = result.PublishedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
        ReleaseNotes = string.IsNullOrWhiteSpace(result.ReleaseNotes) ? string.Empty : result.ReleaseNotes;
        _lastOpenTarget = result.OpenTarget;

        if (!result.IsSuccess)
        {
            HasUpdate = false;
            _hasCheckedUpdates = true;
            UpdateStatusMessage = string.Format(L("Settings.UpdateCheckFailedFormat"), result.ErrorMessage ?? L("Settings.UnknownError"));
            if (showToasts)
                ToastService.Instance.Error(UpdateStatusMessage);
            return;
        }

        _hasCheckedUpdates = true;
        HasUpdate = result.HasUpdate;
        if (result.HasUpdate)
        {
            UpdateStatusMessage = string.Format(
                L("Settings.UpdateAvailableFormat"),
                result.CurrentVersionDisplay,
                result.LatestVersionDisplay ?? result.LatestVersion?.ToString() ?? "-");
            if (showToasts)
                ToastService.Instance.Success(UpdateStatusMessage);
        }
        else
        {
            UpdateStatusMessage = string.Format(L("Settings.UpdateUpToDateFormat"), result.CurrentVersionDisplay);
            if (showToasts)
                ToastService.Instance.Success(UpdateStatusMessage);
        }
    }

    private bool CanOpenUpdateTarget()
        => !IsCheckingUpdates && !string.IsNullOrWhiteSpace(_lastOpenTarget);

    private void OpenUpdateTarget()
    {
        if (string.IsNullOrWhiteSpace(_lastOpenTarget))
        {
            ToastService.Instance.Error(L("Settings.UpdateOpenTargetMissing"));
            return;
        }

        OpenPathOrUrl(_lastOpenTarget!);
    }

    private void OpenConfiguredUpdateSource()
    {
        if (_prefs.UpdateSourceType == UpdateSourceType.GitHub)
        {
            OpenPathOrUrl(GitHubReleasesPage);
            return;
        }

        if (string.IsNullOrWhiteSpace(_prefs.UpdateFolderPath))
        {
            ToastService.Instance.Error(L("Settings.UpdateFolderPathEmpty"));
            return;
        }

        OpenPathOrUrl(_prefs.UpdateFolderPath);
    }

    private static void OpenPathOrUrl(string pathOrUrl)
    {
        try
        {
            var started = Process.Start(new ProcessStartInfo
            {
                FileName = pathOrUrl,
                UseShellExecute = true,
            });

            if (started == null)
                ToastService.Instance.Error(L("Settings.UpdateOpenTargetFailed"));
        }
        catch (Exception ex)
        {
            ToastService.Instance.Error(string.Format(L("Settings.UpdateOpenTargetFailedFormat"), ex.Message));
        }
    }

    private void OpenDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(LocalDataDirectory);
            var started = Process.Start(new ProcessStartInfo
            {
                FileName = LocalDataDirectory,
                UseShellExecute = true,
            });

            if (started == null)
            {
                ToastService.Instance.Error(L("Settings.OpenDataDirFailed"));
                return;
            }
        }
        catch (Exception ex)
        {
            ToastService.Instance.Error(string.Format(L("Settings.OpenDataDirFailedFormat"), ex.Message));
        }
    }

    private void CopyDataDirectory()
    {
        try
        {
            Clipboard.SetText(LocalDataDirectory);
            ToastService.Instance.Success(L("Settings.CopyDataDirSuccess"));
        }
        catch (Exception ex)
        {
            ToastService.Instance.Error(string.Format(L("Settings.CopyDataDirFailedFormat"), ex.Message));
        }
    }

    private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
