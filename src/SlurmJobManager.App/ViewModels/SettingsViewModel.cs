using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using SlurmJobManager.App.Services;
using SlurmJobManager.Core.Services;
using System.Windows;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// Settings page: wraps the connection configuration and exposes app-wide preferences
/// (theme, polling frequency, etc.) through the main view-model.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly AppPreferencesService _prefs;

    public SettingsViewModel(MainViewModel main, AppPreferencesService prefs)
    {
        _main  = main  ?? throw new ArgumentNullException(nameof(main));
        _prefs = prefs ?? throw new ArgumentNullException(nameof(prefs));
    }

    // ── Connection configuration (re-exposed for embedding ConnectionView) ──

    public ConnectionViewModel Connection => _main.Connection;

    // ── Startup auto-connect ──────────────────────────────────────────────

    /// <summary>
    /// When enabled, the app automatically connects using the saved profile on startup.
    /// Persisted via <see cref="AppPreferencesService"/>.
    /// </summary>
    public bool AutoConnectOnStartup
    {
        get => _prefs.AutoConnectOnStartup;
        set
        {
            if (_prefs.TrySetAutoConnectOnStartup(value, out var saveError))
            {
                ToastService.Instance.Success(L("Settings.AutoSaveSuccess"));
            }
            else
            {
                ToastService.Instance.Error(string.Format(
                    L("Settings.AutoSaveFailedFormat"),
                    saveError ?? L("Settings.UnknownError")));
            }
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// When enabled, successful login restores the last task context for the same host/user
    /// and auto-navigates to Tasks if a valid previous TaskId exists.
    /// </summary>
    public bool AutoRestoreLastTaskOnLogin
    {
        get => _prefs.AutoRestoreLastTaskOnLogin;
        set
        {
            if (_prefs.TrySetAutoRestoreLastTaskOnLogin(value, out var saveError))
            {
                ToastService.Instance.Success(L("Settings.AutoSaveSuccess"));
            }
            else
            {
                ToastService.Instance.Error(string.Format(
                    L("Settings.AutoSaveFailedFormat"),
                    saveError ?? L("Settings.UnknownError")));
            }
            OnPropertyChanged();
        }
    }

    // ── Theme ─────────────────────────────────────────────────────────────

    public bool IsDarkTheme
    {
        get => _main.IsDarkTheme;
        set => _main.IsDarkTheme = value;
    }

    public string ThemeLabel => _main.IsDarkTheme
        ? L("Settings.ThemeLight")
        : L("Settings.ThemeDark");

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

    // ── Refresh when theme changes so ThemeLabel stays in sync ───────────

    internal void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(ThemeLabel));
    }

    // ── Locale ────────────────────────────────────────────────────────────

    public string CurrentLocale => _main.CurrentLocale;
    public string CurrentLocaleDisplayName => CurrentLocale == "en-US"
        ? L("Settings.LangEnUS")
        : L("Settings.LangZhCN");

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

    internal void NotifyLocaleChanged()
    {
        OnPropertyChanged(nameof(CurrentLocale));
        OnPropertyChanged(nameof(CurrentLocaleDisplayName));
        OnPropertyChanged(nameof(ThemeLabel));
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

    private static string L(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;
}
