using System.Windows;
using System.Windows.Input;

namespace SlurmJobManager.App.ViewModels;

/// <summary>Root view-model: owns all child VMs, status bar, and theme toggle.</summary>
public sealed class MainViewModel : ViewModelBase
{
    private string _statusMessage = "Ready";
    private bool _isDarkTheme = true;

    public ConnectionViewModel Connection { get; }
    public TaskEditorViewModel TaskEditor { get; }
    public MonitorViewModel    Monitor    { get; }
    public LogViewerViewModel  LogViewer  { get; }
    public ConsoleViewModel    Console    { get; }

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
        }
    }

    public string ThemeToggleLabel => _isDarkTheme ? "☀ Light" : "🌙 Dark";

    public ICommand ToggleThemeCommand { get; }

    public MainViewModel(
        ConnectionViewModel connection,
        TaskEditorViewModel taskEditor,
        MonitorViewModel    monitor,
        LogViewerViewModel  logViewer,
        ConsoleViewModel    console)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        TaskEditor = taskEditor ?? throw new ArgumentNullException(nameof(taskEditor));
        Monitor    = monitor    ?? throw new ArgumentNullException(nameof(monitor));
        LogViewer  = logViewer  ?? throw new ArgumentNullException(nameof(logViewer));
        Console    = console    ?? throw new ArgumentNullException(nameof(console));

        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);
    }

    private static void ApplyTheme(bool dark)
    {
        var app = Application.Current;
        // The first merged dictionary is the theme (Dark/Light); swap it out.
        var dicts   = app.Resources.MergedDictionaries;
        var themeUri = dark
            ? new Uri("pack://application:,,,/SlurmJobManager.App;component/Themes/Dark.xaml")
            : new Uri("pack://application:,,,/SlurmJobManager.App;component/Themes/Light.xaml");

        // Remove existing theme dict and re-insert at position 0
        var existing = dicts.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("/Themes/") == true);
        if (existing != null) dicts.Remove(existing);

        dicts.Insert(0, new ResourceDictionary { Source = themeUri });
    }
}
