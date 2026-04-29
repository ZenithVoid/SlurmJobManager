using System.Windows;
using System.Windows.Input;
using SlurmJobManager.App.Services;

namespace SlurmJobManager.App.ViewModels;

/// <summary>A navigation item shown in the sidebar.</summary>
public sealed record NavItem(string TabId, string Icon, string LocalizationKey);

/// <summary>Root view-model: owns all child VMs, sidebar navigation, and theme toggle.</summary>
public sealed class MainViewModel : ViewModelBase
{
    private string   _statusMessage  = "Ready";
    private bool     _isDarkTheme    = true;
    private string   _activeTab      = "Dashboard";
    private NavItem? _activeNavItem;

    public ConnectionViewModel Connection { get; }
    public TaskEditorViewModel TaskEditor  { get; }
    public MonitorViewModel    Monitor     { get; }
    public LogViewerViewModel  LogViewer   { get; }
    public ConsoleViewModel    Console     { get; }
    public DashboardViewModel  Dashboard   { get; }
    public SettingsViewModel   Settings    { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (!SetField(ref _isDarkTheme, value)) return;
            ApplyTheme(value);
            OnPropertyChanged(nameof(ThemeToggleLabel));
            Settings?.NotifyThemeChanged();
        }
    }

    public string ThemeToggleLabel => _isDarkTheme ? "☀ Light" : "🌙 Dark";

    // ── Sidebar navigation ────────────────────────────────────────────────

    public string ActiveTab
    {
        get => _activeTab;
        set
        {
            if (!SetField(ref _activeTab, value)) return;
            OnPropertyChanged(nameof(ShowDashboard));
            OnPropertyChanged(nameof(ShowTasks));
            OnPropertyChanged(nameof(ShowMonitor));
            OnPropertyChanged(nameof(ShowLogs));
            OnPropertyChanged(nameof(ShowConsole));
            OnPropertyChanged(nameof(ShowSettings));

            // Keep ActiveNavItem in sync when ActiveTab is set directly
            var matching = NavItems?.FirstOrDefault(n => n.TabId == value);
            if (matching != null && !ReferenceEquals(_activeNavItem, matching))
                _activeNavItem = matching;
        }
    }

    /// <summary>
    /// The currently selected <see cref="NavItem"/>; bound two-way to the sidebar ListBox's
    /// <c>SelectedItem</c> to guarantee reliable selection regardless of WPF initialization order.
    /// </summary>
    public NavItem? ActiveNavItem
    {
        get => _activeNavItem;
        set
        {
            if (!SetField(ref _activeNavItem, value)) return;
            if (value != null) ActiveTab = value.TabId;
        }
    }

    public bool ShowDashboard => ActiveTab == "Dashboard";
    public bool ShowTasks     => ActiveTab == "Tasks";
    public bool ShowMonitor   => ActiveTab == "Monitor";
    public bool ShowLogs      => ActiveTab == "Logs";
    public bool ShowConsole   => ActiveTab == "Console";
    public bool ShowSettings  => ActiveTab == "Settings";

    public ICommand ToggleThemeCommand  { get; }
    public ICommand NavigateCommand     { get; }
    public ICommand SwitchLocaleCommand { get; }

    // ── Locale ────────────────────────────────────────────────────────────

    private string _currentLocale = "zh-CN";
    public string CurrentLocale
    {
        get => _currentLocale;
        private set => SetField(ref _currentLocale, value);
    }

    public void ApplyLocale(string locale)
    {
        CurrentLocale = locale;
        var app   = Application.Current;
        var dicts = app.Resources.MergedDictionaries;
        var locUri = new Uri(
            $"pack://application:,,,/SlurmJobManager.App;component/Localization/Strings.{locale}.xaml");

        var existing = dicts.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("/Localization/Strings.") == true);
        if (existing != null) dicts.Remove(existing);

        dicts.Add(new ResourceDictionary { Source = locUri });
        Settings?.NotifyLocaleChanged();
    }

    public MainViewModel(
        ConnectionViewModel connection,
        TaskEditorViewModel taskEditor,
        MonitorViewModel    monitor,
        LogViewerViewModel  logViewer,
        ConsoleViewModel    console,
        AppPreferencesService prefs)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        TaskEditor = taskEditor ?? throw new ArgumentNullException(nameof(taskEditor));
        Monitor    = monitor    ?? throw new ArgumentNullException(nameof(monitor));
        LogViewer  = logViewer  ?? throw new ArgumentNullException(nameof(logViewer));
        Console    = console    ?? throw new ArgumentNullException(nameof(console));

        ToggleThemeCommand  = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);
        NavigateCommand     = new RelayCommand<string>(tab => { if (tab != null) ActiveTab = tab; });
        SwitchLocaleCommand = new RelayCommand<string>(locale => { if (locale != null) ApplyLocale(locale); });

        NavItems = new NavItem[]
        {
            new("Dashboard", "🏠", "Nav.Dashboard"),
            new("Tasks",     "⚡", "Nav.Tasks"),
            new("Monitor",   "📊", "Nav.Monitor"),
            new("Logs",      "📋", "Nav.Logs"),
            new("Console",   "⌨", "Nav.Console"),
            new("Settings",  "⚙", "Nav.Settings"),
        };

        // Set initial selection to Dashboard
        _activeNavItem = NavItems[0];

        Dashboard = new DashboardViewModel(connection, monitor, tab => ActiveTab = tab);
        Settings  = new SettingsViewModel(this, prefs);
    }

    private static void ApplyTheme(bool dark)
    {
        var app   = Application.Current;
        var dicts = app.Resources.MergedDictionaries;
        var themeUri = dark
            ? new Uri("pack://application:,,,/SlurmJobManager.App;component/Themes/Dark.xaml")
            : new Uri("pack://application:,,,/SlurmJobManager.App;component/Themes/Light.xaml");

        var existing = dicts.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("/Themes/") == true);
        if (existing != null) dicts.Remove(existing);

        dicts.Insert(0, new ResourceDictionary { Source = themeUri });
    }
}

