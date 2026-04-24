using System.Windows.Input;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// Settings page: wraps the connection configuration and exposes app-wide preferences
/// (theme, polling frequency, etc.) through the main view-model.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public SettingsViewModel(MainViewModel main)
        => _main = main ?? throw new ArgumentNullException(nameof(main));

    // ── Connection configuration (re-exposed for embedding ConnectionView) ──

    public ConnectionViewModel Connection => _main.Connection;

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
        set => _main.Monitor.PollIntervalSeconds = value;
    }

    // ── Refresh when theme changes so ThemeLabel stays in sync ───────────

    internal void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(ThemeLabel));
    }
}
