using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using SlurmPilot.App.Services.Plugins;

namespace SlurmPilot.App.ViewModels;

public sealed class PluginItemViewModel : ViewModelBase
{
    private readonly LoadedPlugin _plugin;
    private FrameworkElement? _view;
    private bool _isActive;
    public PluginItemViewModel(LoadedPlugin plugin) => _plugin = plugin;
    public string Id => _plugin.Id;
    public string DisplayName => _plugin.DisplayName;
    public string Description => _plugin.Description;
    public string Icon => _plugin.Icon;
    public string AssemblyPath => _plugin.AssemblyPath;
    public FrameworkElement? View { get => _view; private set => SetField(ref _view, value); }
    public bool IsActive { get => _isActive; set => SetField(ref _isActive, value); }
    public void Activate() => View ??= _plugin.Activate();
    public void DetachView() => View = null;
}

public sealed class PluginHostViewModel : ViewModelBase, IDisposable
{
    private readonly PluginManager _pluginManager;
    private PluginItemViewModel? _selectedPlugin;
    private string _statusText = string.Empty;

    public PluginHostViewModel(PluginManager pluginManager)
    {
        _pluginManager = pluginManager;
        ReloadCommand = new RelayCommand(Reload);
        ActivatePluginCommand = new RelayCommand<PluginItemViewModel>(ActivatePlugin, p => p != null);
        UnloadPluginCommand = new RelayCommand<PluginItemViewModel>(UnloadPlugin, p => p != null);
        Reload();
    }

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];
    public PluginItemViewModel? SelectedPlugin
    {
        get => _selectedPlugin;
        set
        {
            if (!SetField(ref _selectedPlugin, value)) return;
            foreach (var plugin in Plugins) plugin.IsActive = ReferenceEquals(plugin, value);
            if (value != null)
            {
                try { value.Activate(); StatusText = $"{Plugins.Count} plugin(s) loaded"; }
                catch (Exception ex) { StatusText = $"Failed to activate {value.DisplayName}: {ex.Message}"; }
            }
            SelectedPluginChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public string PluginDirectory => _pluginManager.PluginDirectory;
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public ICommand ReloadCommand { get; }
    public ICommand ActivatePluginCommand { get; }
    public ICommand UnloadPluginCommand { get; }
    public event EventHandler? SelectedPluginChanged;

    public void Dispose()
    {
        SelectedPlugin = null;
        foreach (var plugin in Plugins) plugin.DetachView();
        Plugins.Clear();
        _pluginManager.Dispose();
    }

    private void Reload()
    {
        SelectedPlugin = null;
        foreach (var plugin in Plugins) plugin.DetachView();
        Plugins.Clear();
        foreach (var plugin in _pluginManager.Reload()) Plugins.Add(new PluginItemViewModel(plugin));
        StatusText = $"{Plugins.Count} plugin(s) loaded";
        if (_pluginManager.LoadErrors.Count > 0) StatusText += $", {_pluginManager.LoadErrors.Count} failed";
        OnPropertyChanged(nameof(PluginDirectory));
    }

    private void UnloadPlugin(PluginItemViewModel? plugin)
    {
        if (plugin == null) return;
        if (ReferenceEquals(SelectedPlugin, plugin)) SelectedPlugin = null;
        plugin.DetachView();
        Plugins.Remove(plugin);
        _pluginManager.Unload(plugin.Id);
        StatusText = $"{Plugins.Count} plugin(s) loaded";
    }

    private void ActivatePlugin(PluginItemViewModel? plugin)
    {
        if (plugin != null) SelectedPlugin = plugin;
    }
}
