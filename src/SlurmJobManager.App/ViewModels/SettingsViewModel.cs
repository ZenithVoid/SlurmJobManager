using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;
using SlurmJobManager.App.Services;
using SlurmJobManager.App.Services.ExternalTargets;
using SlurmJobManager.App.Services.Logging;
using SlurmJobManager.App.Services.Packaging;
using SlurmJobManager.App.Services.Updates;
using SlurmJobManager.App.Views.Dialogs;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Services;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// Settings page: wraps the connection configuration and exposes app-wide preferences
/// (theme, polling frequency, etc.) through the main view-model.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private enum ConnectivityTestStatus
    {
        Neutral,
        Success,
        Warning,
        Failure,
    }

    private const string GitHubReleasesPage = "https://github.com/ZenithVoid/SlurmJobManager/releases";
    private const string GitHubRepositoryPage = "https://github.com/ZenithVoid/SlurmJobManager";
    private const int ReleaseScriptSearchMaxDepth = 8;
    private const long SlowConnectionThresholdMs = 5000;
    private static readonly TimeSpan UpdateLaunchGracePeriod = TimeSpan.FromMilliseconds(250);

    private readonly MainViewModel _main;
    private readonly AppPreferencesService _prefs;
    private readonly IUpdateCheckService _updateCheckService;
    private readonly IApplicationVersionService _versionService;
    private readonly IUpdateLaunchService _updateLaunchService;
    private readonly ILogFileService _logFileService;
    private readonly IExternalTargetOpener _externalTargetOpener;
    private readonly IAppLogger? _logger;
    private readonly PackagingFeatureAuthorizationResult _packagingAuthorizationResult;

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
    private Version? _latestVersion;
    private string _releasePackagingPublishDirectory = string.Empty;
    private string _releasePackagingOutputDirectory = string.Empty;
    private string _releasePackagingNotes = string.Empty;
    private string _releasePackagingStatusMessage;
    private bool _isGeneratingReleasePackage;
    private string _updateCustomProxyPortText;
    private bool _isTestingUpdateConnectivity;
    private ConnectivityTestStatus _updateConnectivityStatus = ConnectivityTestStatus.Neutral;
    private string _updateConnectionTestStatusMessage = "-";
    private string _updateConnectionTestTarget = "-";
    private string _updateConnectionTestProxyPolicy = "-";
    private string _updateConnectionTestDuration = "-";
    private string _updateConnectionTestErrorSummary = string.Empty;
    private string _updateConnectionTestSuggestion = string.Empty;

    public SettingsViewModel(
        MainViewModel main,
        AppPreferencesService prefs,
        IUpdateCheckService updateCheckService,
        IApplicationVersionService versionService,
        IUpdateLaunchService updateLaunchService,
        IPackagingFeatureAuthorizationService packagingFeatureAuthorizationService,
        ILogFileService logFileService,
        IExternalTargetOpener externalTargetOpener,
        IAppLogger? logger = null)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _prefs = prefs ?? throw new ArgumentNullException(nameof(prefs));
        _updateCheckService = updateCheckService ?? throw new ArgumentNullException(nameof(updateCheckService));
        _versionService = versionService ?? throw new ArgumentNullException(nameof(versionService));
        _updateLaunchService = updateLaunchService ?? throw new ArgumentNullException(nameof(updateLaunchService));
        _logFileService = logFileService ?? throw new ArgumentNullException(nameof(logFileService));
        _externalTargetOpener = externalTargetOpener ?? throw new ArgumentNullException(nameof(externalTargetOpener));
        _logger = logger;
        _packagingAuthorizationResult = (packagingFeatureAuthorizationService ?? throw new ArgumentNullException(nameof(packagingFeatureAuthorizationService))).EvaluateAuthorization();

        _updateStatusMessage = L("Settings.UpdateNotCheckedYet");
        _updateSourceDisplay = L("Settings.UpdateSourceUnknown");
        _releasePackagingStatusMessage = L("Settings.ReleasePackagingReady");
        _releasePackagingOutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        _updateCustomProxyPortText = _prefs.UpdateCustomProxyPort?.ToString() ?? string.Empty;
        _updateConnectionTestStatusMessage = L("Settings.UpdateConnectionTestNotRun");

        CheckForUpdatesCommand = new AsyncRelayCommand(
            () => CheckForUpdatesAsync(showToasts: true),
            () => !IsCheckingUpdates && !IsTestingUpdateConnectivity);
        TestUpdateConnectivityCommand = new AsyncRelayCommand(
            () => TestUpdateConnectivityAsync(showToasts: true),
            () => !IsCheckingUpdates && !IsTestingUpdateConnectivity);
        LaunchUpdateCommand = new AsyncRelayCommand(LaunchUpdateAsync, CanLaunchUpdate);
        OpenUpdateTargetCommand = new RelayCommand(OpenUpdateTarget, CanOpenUpdateTarget);
        OpenUpdateSourceCommand = new RelayCommand(OpenConfiguredUpdateSource);
        GenerateReleasePackageCommand = new AsyncRelayCommand(GenerateReleasePackageAsync, CanGenerateReleasePackage);
        OpenReleasePackageOutputDirectoryCommand = new RelayCommand(OpenReleasePackageOutputDirectory, CanOpenReleasePackageOutputDirectory);
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
    public ICommand OpenLogDirectoryCommand => new RelayCommand(OpenLogDirectory);
    public ICommand ViewRecentLogCommand => new RelayCommand(ViewRecentLog);
    public ICommand ExportLogsCommand => new RelayCommand(ExportLogs);
    public ICommand OpenAboutPageCommand => new RelayCommand(OpenAboutPage);
    public ICommand OpenRepositoryCommand => new RelayCommand(OpenRepository);
    public ICommand GenerateReleasePackageCommand { get; }
    public ICommand OpenReleasePackageOutputDirectoryCommand { get; }

    // ── Local data paths ───────────────────────────────────────────────────

    public string LocalDataDirectory => LocalDataPaths.DataDirectory;
    public string LogsDirectory => _logFileService.LogsDirectory;
    public string TasksDirectory => LocalDataPaths.TasksDirectory;
    public string BlueprintsDirectory => LocalDataPaths.BlueprintsDirectory;
    public string RecentConnectionsFilePath => LocalDataPaths.RecentConnectionsFilePath;
    public string PreferencesFilePath => LocalDataPaths.PreferencesFilePath;
    public string LastTaskContextsFilePath => LocalDataPaths.LastTaskContextsFilePath;

    // ── Release packaging ───────────────────────────────────────────────────

    public bool IsReleasePackagingAuthorized => _packagingAuthorizationResult.IsAuthorized;

    public string ReleasePackagingPublishDirectory
    {
        get => _releasePackagingPublishDirectory;
        set => SetField(ref _releasePackagingPublishDirectory, value);
    }

    public string ReleasePackagingOutputDirectory
    {
        get => _releasePackagingOutputDirectory;
        set => SetField(ref _releasePackagingOutputDirectory, value);
    }

    public string ReleasePackagingNotes
    {
        get => _releasePackagingNotes;
        set => SetField(ref _releasePackagingNotes, value);
    }

    public string ReleasePackagingStatusMessage
    {
        get => _releasePackagingStatusMessage;
        private set => SetField(ref _releasePackagingStatusMessage, value);
    }

    public bool IsGeneratingReleasePackage
    {
        get => _isGeneratingReleasePackage;
        private set
        {
            if (!SetField(ref _isGeneratingReleasePackage, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

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

    public bool UseProxyForUpdates
    {
        get => _prefs.UseProxyForUpdates;
        set
        {
            if (_prefs.TrySetUseProxyForUpdates(value, out var saveError))
                ToastService.Instance.Success(L("Settings.AutoSaveSuccess"));
            else
                ToastService.Instance.Error(string.Format(L("Settings.AutoSaveFailedFormat"), saveError ?? L("Settings.UnknownError")));
            OnPropertyChanged();
        }
    }

    public int SelectedUpdateProxyModeIndex
    {
        get => _prefs.UpdateProxyMode switch
        {
            UpdateProxyMode.SystemProxy => 1,
            UpdateProxyMode.CustomProxy => 2,
            _ => 0,
        };
        set
        {
            var newMode = value switch
            {
                1 => UpdateProxyMode.SystemProxy,
                2 => UpdateProxyMode.CustomProxy,
                _ => UpdateProxyMode.NoProxy,
            };

            if (_prefs.TrySetUpdateProxyMode(newMode, out var saveError))
                ToastService.Instance.Success(L("Settings.AutoSaveSuccess"));
            else
                ToastService.Instance.Error(string.Format(L("Settings.AutoSaveFailedFormat"), saveError ?? L("Settings.UnknownError")));

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomUpdateProxyMode));
            OnPropertyChanged(nameof(IsUpdateProxyDetailVisible));
            OnPropertyChanged(nameof(ShouldShowCustomProxyFields));
        }
    }

    public bool IsCustomUpdateProxyMode => _prefs.UpdateProxyMode == UpdateProxyMode.CustomProxy;
    public bool IsUpdateProxyDetailVisible => _prefs.UpdateProxyMode != UpdateProxyMode.NoProxy;
    public bool ShouldShowCustomProxyFields => _prefs.UpdateProxyMode == UpdateProxyMode.CustomProxy;

    public string UpdateCustomProxyHost
    {
        get => _prefs.UpdateCustomProxyHost;
        set
        {
            if (_prefs.TrySetUpdateCustomProxyHost(value, out var saveError))
                ToastService.Instance.Success(L("Settings.AutoSaveSuccess"));
            else
                ToastService.Instance.Error(string.Format(L("Settings.AutoSaveFailedFormat"), saveError ?? L("Settings.UnknownError")));
            OnPropertyChanged();
        }
    }

    public string UpdateCustomProxyPortText
    {
        get => _updateCustomProxyPortText;
        set
        {
            var normalized = (value ?? string.Empty).Trim();
            _updateCustomProxyPortText = normalized;

            if (string.IsNullOrWhiteSpace(normalized))
            {
                if (_prefs.TrySetUpdateCustomProxyPort(null, out var emptySaveError))
                    ToastService.Instance.Success(L("Settings.AutoSaveSuccess"));
                else
                    ToastService.Instance.Error(string.Format(L("Settings.AutoSaveFailedFormat"), emptySaveError ?? L("Settings.UnknownError")));
                OnPropertyChanged();
                return;
            }

            if (!int.TryParse(normalized, out var port) || port < 1 || port > 65535)
            {
                ToastService.Instance.Error(L("Settings.ProxyPortInvalid"));
                OnPropertyChanged();
                return;
            }

            if (_prefs.TrySetUpdateCustomProxyPort(port, out var saveError))
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

    public bool IsTestingUpdateConnectivity
    {
        get => _isTestingUpdateConnectivity;
        private set => SetField(ref _isTestingUpdateConnectivity, value);
    }

    public string UpdateConnectionTestStatusMessage
    {
        get => _updateConnectionTestStatusMessage;
        private set => SetField(ref _updateConnectionTestStatusMessage, value);
    }

    public string UpdateConnectionTestStatusTone => _updateConnectivityStatus.ToString();

    public string UpdateConnectionTestTarget
    {
        get => _updateConnectionTestTarget;
        private set => SetField(ref _updateConnectionTestTarget, value);
    }

    public string UpdateConnectionTestProxyPolicy
    {
        get => _updateConnectionTestProxyPolicy;
        private set => SetField(ref _updateConnectionTestProxyPolicy, value);
    }

    public string UpdateConnectionTestDuration
    {
        get => _updateConnectionTestDuration;
        private set => SetField(ref _updateConnectionTestDuration, value);
    }

    public string UpdateConnectionTestErrorSummary
    {
        get => _updateConnectionTestErrorSummary;
        private set => SetField(ref _updateConnectionTestErrorSummary, value);
    }

    public string UpdateConnectionTestSuggestion
    {
        get => _updateConnectionTestSuggestion;
        private set => SetField(ref _updateConnectionTestSuggestion, value);
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
    public ICommand TestUpdateConnectivityCommand { get; }
    public ICommand LaunchUpdateCommand { get; }
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
        if (_updateConnectivityStatus == ConnectivityTestStatus.Neutral)
            UpdateConnectionTestStatusMessage = L("Settings.UpdateConnectionTestNotRun");
    }

    private async Task CheckForUpdatesAsync(bool showToasts)
    {
        if (!TryValidateUpdateProxyConfiguration(out var proxyValidationError))
        {
            UpdateStatusMessage = proxyValidationError;
            if (showToasts)
                ToastService.Instance.Error(UpdateStatusMessage);
            return;
        }

        _logger?.Info(
            $"Update check requested. Source={_prefs.UpdateSourceType}, IncludePrerelease={_prefs.IncludePrereleaseUpdates}, UseProxyForUpdates={_prefs.UseProxyForUpdates}, ProxyMode={_prefs.UpdateProxyMode}");
        try
        {
            IsCheckingUpdates = true;
            UpdateStatusMessage = L("Settings.UpdateChecking");
            CommandManager.InvalidateRequerySuggested();

            var result = await _updateCheckService.CheckForUpdatesAsync(BuildUpdateCheckRequest());

            ApplyUpdateResult(result, showToasts);
        }
        catch (Exception ex)
        {
            _logger?.Error("Update check failed unexpectedly.", ex);
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

    private async Task TestUpdateConnectivityAsync(bool showToasts)
    {
        if (!TryValidateUpdateProxyConfiguration(out var proxyValidationError))
        {
            UpdateConnectionTestStatusMessage = proxyValidationError;
            UpdateConnectionTestErrorSummary = proxyValidationError;
            _updateConnectivityStatus = ConnectivityTestStatus.Failure;
            OnPropertyChanged(nameof(UpdateConnectionTestStatusTone));
            if (showToasts)
                ToastService.Instance.Error(proxyValidationError);
            return;
        }

        var request = BuildUpdateCheckRequest();
        _logger?.Info(
            $"Update connectivity test requested. Source={request.SourceType}, UseProxyForUpdates={request.UseProxyForUpdates}, ProxyMode={request.ProxyMode}");

        try
        {
            IsTestingUpdateConnectivity = true;
            UpdateConnectionTestStatusMessage = L("Settings.UpdateConnectionTesting");
            UpdateConnectionTestErrorSummary = string.Empty;
            UpdateConnectionTestSuggestion = string.Empty;
            _updateConnectivityStatus = ConnectivityTestStatus.Neutral;
            OnPropertyChanged(nameof(UpdateConnectionTestStatusTone));
            CommandManager.InvalidateRequerySuggested();

            var result = await _updateCheckService.TestConnectivityAsync(request);
            ApplyConnectivityTestResult(result, showToasts);
        }
        catch (Exception ex)
        {
            _logger?.Error("Update connectivity test failed unexpectedly.", ex);
            UpdateConnectionTestStatusMessage = string.Format(L("Settings.UpdateConnectionTestFailedFormat"), ex.Message);
            UpdateConnectionTestErrorSummary = ex.Message;
            UpdateConnectionTestSuggestion = L("Settings.UpdateConnectionTestFallbackSuggestion");
            _updateConnectivityStatus = ConnectivityTestStatus.Failure;
            OnPropertyChanged(nameof(UpdateConnectionTestStatusTone));
            if (showToasts)
                ToastService.Instance.Error(UpdateConnectionTestStatusMessage);
        }
        finally
        {
            IsTestingUpdateConnectivity = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private bool TryValidateUpdateProxyConfiguration(out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!_prefs.UseProxyForUpdates || _prefs.UpdateProxyMode != UpdateProxyMode.CustomProxy)
            return true;

        if (UpdateProxyValidation.TryValidateCustomProxy(
                _prefs.UpdateCustomProxyHost,
                _prefs.UpdateCustomProxyPort,
                out _))
        {
            return true;
        }

        errorMessage = L("Settings.ProxyConfigInvalid");
        return false;
    }

    private UpdateCheckRequest BuildUpdateCheckRequest()
    {
        return new UpdateCheckRequest(
            _prefs.UpdateSourceType,
            _prefs.IncludePrereleaseUpdates,
            _prefs.UpdateFolderPath,
            _prefs.UseProxyForUpdates,
            _prefs.UpdateProxyMode,
            _prefs.UpdateCustomProxyHost,
            _prefs.UpdateCustomProxyPort);
    }

    private void ApplyConnectivityTestResult(UpdateConnectivityTestResult result, bool showToasts)
    {
        _logger?.Info(
            $"Update connectivity test completed. Success={result.IsSuccess}, Source={result.SourceType}, Target={result.Target}, ProxyPolicy={result.EffectiveProxyPolicy}, DurationMs={result.DurationMs}");

        var isSlow = result.IsSuccess && result.DurationMs >= SlowConnectionThresholdMs;
        _updateConnectivityStatus = result.IsSuccess
            ? (isSlow ? ConnectivityTestStatus.Warning : ConnectivityTestStatus.Success)
            : ConnectivityTestStatus.Failure;
        OnPropertyChanged(nameof(UpdateConnectionTestStatusTone));

        UpdateConnectionTestStatusMessage = isSlow
            ? $"{result.Summary} {L("Settings.UpdateConnectionSlowWarning")}"
            : result.Summary;
        UpdateConnectionTestTarget = result.Target;
        UpdateConnectionTestProxyPolicy = result.SourceType == UpdateSourceType.Folder
            ? $"{result.EffectiveProxyPolicy}; {L("Settings.UpdateConnectionFolderProxyNote")}"
            : result.EffectiveProxyPolicy;
        UpdateConnectionTestDuration = $"{result.DurationMs} ms";
        UpdateConnectionTestErrorSummary = result.ErrorSummary ?? string.Empty;
        UpdateConnectionTestSuggestion = result.Suggestion ?? string.Empty;

        if (!showToasts)
            return;

        if (!result.IsSuccess)
        {
            var toastMessage = string.IsNullOrWhiteSpace(result.ErrorSummary)
                ? UpdateConnectionTestStatusMessage
                : $"{UpdateConnectionTestStatusMessage} {result.ErrorSummary}";
            ToastService.Instance.Error(toastMessage);
            return;
        }

        if (isSlow)
            ToastService.Instance.Warning(UpdateConnectionTestStatusMessage);
        else
            ToastService.Instance.Success(UpdateConnectionTestStatusMessage);
    }

    private void ApplyUpdateResult(UpdateCheckResult result, bool showToasts)
    {
        _logger?.Info($"Update check completed. Success={result.IsSuccess}, HasUpdate={result.HasUpdate}, Source={result.SourceType}");
        UpdateSourceDisplay = result.SourceType == UpdateSourceType.GitHub
            ? L("Settings.UpdateSourceGitHub")
            : L("Settings.UpdateSourceFolder");
        LatestVersionDisplay = result.LatestVersionDisplay ?? "-";
        _latestVersion = result.LatestVersion;
        ReleaseTitle = string.IsNullOrWhiteSpace(result.ReleaseTitle) ? "-" : result.ReleaseTitle;
        ReleasePublishedAt = result.PublishedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
        ReleaseNotes = string.IsNullOrWhiteSpace(result.ReleaseNotes) ? string.Empty : result.ReleaseNotes;
        _lastOpenTarget = result.OpenTarget;

        if (!result.IsSuccess)
        {
            HasUpdate = false;
            _latestVersion = null;
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

    private bool CanLaunchUpdate()
        => !IsCheckingUpdates && HasUpdate && !string.IsNullOrWhiteSpace(_lastOpenTarget);

    private async Task LaunchUpdateAsync()
    {
        if (!TryResolveLocalUpdatePackage(_lastOpenTarget, _latestVersion, out var packagePath, out var packageError, out var selectionMessage))
        {
            _logger?.Warning($"Update launch aborted because target is invalid. Detail={packageError}");
            var message = string.Format(L("Settings.UpdateLaunchInvalidTargetFormat"), packageError ?? L("Settings.UnknownError"));
            UpdateStatusMessage = message;
            ToastService.Instance.Error(message);
            return;
        }

        if (!string.IsNullOrWhiteSpace(selectionMessage))
            ToastService.Instance.Info(selectionMessage);

        if (!_updateLaunchService.TryCreateLaunchRequest(
                packagePath!,
                restartMainApplication: true,
                restartArguments: null,
                targetVersionDisplay: _latestVersion?.ToString(3),
                out var request,
                out var createError))
        {
            _logger?.Warning($"Update launch request creation failed. Package={packagePath}, Error={createError}");
            var message = string.Format(L("Settings.UpdateLaunchFailedFormat"), createError ?? L("Settings.UnknownError"));
            UpdateStatusMessage = message;
            ToastService.Instance.Error(message);
            return;
        }

        var launchResult = _updateLaunchService.LaunchUpdater(request!);
        if (!launchResult.IsSuccess)
        {
            _logger?.Error($"Failed to launch updater. Target={packagePath}, Error={launchResult.ErrorMessage}");
            var message = string.Format(L("Settings.UpdateLaunchFailedFormat"), launchResult.ErrorMessage ?? L("Settings.UnknownError"));
            UpdateStatusMessage = message;
            ToastService.Instance.Error(message);
            return;
        }

        _logger?.Info($"Updater launched successfully. UpdaterPath={launchResult.UpdaterPath}, Package={packagePath}");
        UpdateStatusMessage = L("Settings.UpdateLaunchingAndClosing");
        ToastService.Instance.Success(L("Settings.UpdateLaunchSuccess"));
        await Task.Delay(UpdateLaunchGracePeriod);
        Application.Current?.Dispatcher.BeginInvoke(() => Application.Current?.MainWindow?.Close());
    }

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

    private bool TryOpenPathOrUrl(string pathOrUrl, string failedResourceKey, string failedFormatResourceKey)
    {
        if (_externalTargetOpener.TryOpen(pathOrUrl, out var errorMessage))
            return true;

        if (string.IsNullOrWhiteSpace(errorMessage))
            ToastService.Instance.Error(L(failedResourceKey));
        else
            ToastService.Instance.Error(string.Format(L(failedFormatResourceKey), errorMessage));
        return false;
    }

    private void OpenPathOrUrl(string pathOrUrl)
    {
        TryOpenPathOrUrl(pathOrUrl, "Settings.UpdateOpenTargetFailed", "Settings.UpdateOpenTargetFailedFormat");
    }

    private void OpenDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(LocalDataDirectory);
            TryOpenPathOrUrl(LocalDataDirectory, "Settings.OpenDataDirFailed", "Settings.OpenDataDirFailedFormat");
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

    private void OpenLogDirectory()
    {
        try
        {
            _logFileService.EnsureLogsDirectory();
            if (!TryOpenPathOrUrl(_logFileService.LogsDirectory, "Settings.OpenLogsDirFailed", "Settings.OpenLogsDirFailedFormat"))
                return;

            _logger?.Info($"Opened logs directory: {_logFileService.LogsDirectory}");
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to open logs directory: {_logFileService.LogsDirectory}", ex);
            ToastService.Instance.Error(string.Format(L("Settings.OpenLogsDirFailedFormat"), ex.Message));
        }
    }

    private void ViewRecentLog()
    {
        try
        {
            var latest = _logFileService.GetLatestAppLogFilePath();
            if (string.IsNullOrWhiteSpace(latest))
            {
                ToastService.Instance.Warning(L("Settings.RecentLogNotFound"));
                return;
            }

            var content = _logFileService.ReadFileText(latest);
            var viewer = new RecentLogViewerWindow(
                L("Settings.RecentLogViewerTitle"),
                latest,
                content);

            if (Application.Current.MainWindow is { } mainWindow)
                viewer.Owner = mainWindow;

            viewer.ShowDialog();
            _logger?.Info($"Viewed recent log file: {latest}");
        }
        catch (Exception ex)
        {
            _logger?.Error("Failed to load recent log for viewer.", ex);
            ToastService.Instance.Error(string.Format(L("Settings.RecentLogOpenFailedFormat"), ex.Message));
        }
    }

    private void ExportLogs()
    {
        try
        {
            _logFileService.EnsureLogsDirectory();
            var defaultZipName = $"SlurmJobManager-logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
            var saveDialog = new SaveFileDialog
            {
                Title = L("Settings.ExportLogsDialogTitle"),
                Filter = "Zip files (*.zip)|*.zip",
                DefaultExt = ".zip",
                AddExtension = true,
                FileName = defaultZipName,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            };

            if (saveDialog.ShowDialog() != true)
                return;

            var path = _logFileService.ExportLogsZip(saveDialog.FileName);
            _logger?.Info($"Exported logs archive: {path}");
            ToastService.Instance.Success(string.Format(L("Settings.ExportLogsSuccessFormat"), path));
        }
        catch (Exception ex)
        {
            _logger?.Error("Failed to export logs.", ex);
            ToastService.Instance.Error(string.Format(L("Settings.ExportLogsFailedFormat"), ex.Message));
        }
    }

    private void OpenAboutPage()
    {
        _main.ActiveTab = "About";
    }

    private void OpenRepository()
    {
        OpenPathOrUrl(GitHubRepositoryPage);
    }

    private static string L(string key) => Application.Current?.TryFindResource(key) as string ?? key;

    private static bool TryResolveLocalUpdatePackage(
        string? openTarget,
        Version? preferredVersion,
        out string? packagePath,
        out string? errorMessage,
        out string? selectionMessage)
    {
        packagePath = null;
        errorMessage = null;
        selectionMessage = null;

        if (string.IsNullOrWhiteSpace(openTarget))
        {
            errorMessage = "No update target is available.";
            return false;
        }

        var target = openTarget.Trim();
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            errorMessage = "The update target is a remote URL. Download the package locally first.";
            return false;
        }

        if (File.Exists(target))
        {
            if (!UpdatePackageNaming.IsSupportedPackage(target))
            {
                errorMessage = "The update target file is unsupported. Only .zip/.exe/.msi are supported.";
                return false;
            }

            packagePath = target;
            return true;
        }

        if (!Directory.Exists(target))
        {
            errorMessage = $"Update target does not exist: {target}";
            return false;
        }

        packagePath = UpdatePackageNaming.ResolveBestPackagePath(target, preferredVersion, out var hasMultipleCandidates);
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            errorMessage = "No supported update package (.zip/.exe/.msi) was found in the update target directory.";
            return false;
        }

        if (hasMultipleCandidates)
            selectionMessage = string.Format(L("Settings.UpdatePackageAutoSelectedFormat"), Path.GetFileName(packagePath));
        return true;
    }

    private bool CanGenerateReleasePackage()
        => IsReleasePackagingAuthorized
           && !IsGeneratingReleasePackage
           && !string.IsNullOrWhiteSpace(ReleasePackagingPublishDirectory)
           && !string.IsNullOrWhiteSpace(ReleasePackagingOutputDirectory);

    private bool CanOpenReleasePackageOutputDirectory()
        => IsReleasePackagingAuthorized
           && !string.IsNullOrWhiteSpace(ReleasePackagingOutputDirectory);

    private async Task GenerateReleasePackageAsync()
    {
        if (!IsReleasePackagingAuthorized)
            return;

        var publishDirectory = (ReleasePackagingPublishDirectory ?? string.Empty).Trim();
        var outputDirectory = (ReleasePackagingOutputDirectory ?? string.Empty).Trim();

        if (!Directory.Exists(publishDirectory))
        {
            ReleasePackagingStatusMessage = string.Format(L("Settings.ReleasePackagingPublishDirMissingFormat"), publishDirectory);
            return;
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
            IsGeneratingReleasePackage = true;
            ReleasePackagingStatusMessage = L("Settings.ReleasePackagingRunning");
            _logger?.Info("Starting release packaging generation by script.");

            var scriptPath = ResolveReleaseArtifactsScriptPath();
            if (string.IsNullOrWhiteSpace(scriptPath))
            {
                ReleasePackagingStatusMessage = L("Settings.ReleasePackagingScriptMissing");
                _logger?.Warning("Release packaging script was not found from current app location.");
                return;
            }

            var invocationResult = await RunReleaseScriptAsync(scriptPath, publishDirectory, outputDirectory, ReleasePackagingNotes);
            if (!invocationResult.IsSuccess)
            {
                var errorText = string.IsNullOrWhiteSpace(invocationResult.ErrorMessage)
                    ? L("Settings.UnknownError")
                    : invocationResult.ErrorMessage;
                ReleasePackagingStatusMessage = string.Format(L("Settings.ReleasePackagingFailedFormat"), errorText);
                _logger?.Warning($"Release packaging script failed. Error={errorText}");
                return;
            }

            ReleasePackagingStatusMessage = string.Format(L("Settings.ReleasePackagingSuccessFormat"), outputDirectory);
            _logger?.Info($"Release packaging completed. Output={outputDirectory}");
            ToastService.Instance.Success(ReleasePackagingStatusMessage);
        }
        catch (Exception ex)
        {
            _logger?.Error("Release packaging failed unexpectedly.", ex);
            ReleasePackagingStatusMessage = string.Format(L("Settings.ReleasePackagingFailedFormat"), ex.Message);
            ToastService.Instance.Error(ReleasePackagingStatusMessage);
        }
        finally
        {
            IsGeneratingReleasePackage = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void OpenReleasePackageOutputDirectory()
    {
        if (!IsReleasePackagingAuthorized || string.IsNullOrWhiteSpace(ReleasePackagingOutputDirectory))
            return;

        var outputDirectory = ReleasePackagingOutputDirectory.Trim();
        try
        {
            Directory.CreateDirectory(outputDirectory);
            if (!TryOpenPathOrUrl(outputDirectory, "Settings.ReleasePackagingOpenOutputFailed", "Settings.ReleasePackagingOpenOutputFailedFormat"))
                ReleasePackagingStatusMessage = L("Settings.ReleasePackagingOpenOutputFailed");
        }
        catch (Exception ex)
        {
            ReleasePackagingStatusMessage = string.Format(L("Settings.ReleasePackagingOpenOutputFailedFormat"), ex.Message);
        }
    }

    private static async Task<ScriptInvocationResult> RunReleaseScriptAsync(
        string scriptPath,
        string publishDirectory,
        string outputDirectory,
        string notes)
    {
        var result = await TryRunPowerShellAsync(
            "pwsh",
            scriptPath,
            publishDirectory,
            outputDirectory,
            notes);
        if (result.IsSuccess || !result.IsExecutableMissing)
            return result;

        return await TryRunPowerShellAsync(
            "powershell",
            scriptPath,
            publishDirectory,
            outputDirectory,
            notes);
    }

    private static async Task<ScriptInvocationResult> TryRunPowerShellAsync(
        string executable,
        string scriptPath,
        string publishDirectory,
        string outputDirectory,
        string notes)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(scriptPath);
        process.StartInfo.ArgumentList.Add("-PublishDirectory");
        process.StartInfo.ArgumentList.Add(publishDirectory);
        process.StartInfo.ArgumentList.Add("-OutputDirectory");
        process.StartInfo.ArgumentList.Add(outputDirectory);
        process.StartInfo.ArgumentList.Add("-RuntimeIdentifier");
        process.StartInfo.ArgumentList.Add("win-x64");
        process.StartInfo.ArgumentList.Add("-Notes");
        process.StartInfo.ArgumentList.Add(notes ?? string.Empty);

        try
        {
            if (!process.Start())
                return new ScriptInvocationResult(false, false, "Failed to start release script process.");

            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;

            if (process.ExitCode == 0)
                return new ScriptInvocationResult(true, false, stdOut);

            var failureMessage = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
            return new ScriptInvocationResult(false, false, failureMessage);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new ScriptInvocationResult(false, true, ex.Message);
        }
    }

    private static string? ResolveReleaseArtifactsScriptPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < ReleaseScriptSearchMaxDepth && current is not null; i++)
        {
            var candidate = Path.Combine(current.FullName, "scripts", "Generate-ReleaseArtifacts.ps1");
            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        return null;
    }

    private sealed record ScriptInvocationResult(bool IsSuccess, bool IsExecutableMissing, string? ErrorMessage);
}
