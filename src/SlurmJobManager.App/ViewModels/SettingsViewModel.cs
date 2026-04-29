using System.Windows.Input;
using SlurmJobManager.App.Services;

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
            _prefs.AutoConnectOnStartup = value;
            OnPropertyChanged();
        }
    }

    // ── Theme ─────────────────────────────────────────────────────────────

    public bool IsDarkTheme
    {
        get => _main.IsDarkTheme;
        set => _main.IsDarkTheme = value;
    }

    public string ThemeLabel => _main.IsDarkTheme ? "☀ Switch to Light" : "🌙 Switch to Dark";

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

    public ICommand SwitchLocaleCommand => _main.SwitchLocaleCommand;

    internal void NotifyLocaleChanged()
        => OnPropertyChanged(nameof(CurrentLocale));
}
