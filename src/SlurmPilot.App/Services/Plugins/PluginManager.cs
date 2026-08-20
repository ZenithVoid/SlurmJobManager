using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using SlurmPilot.Core.Interfaces;
using SlurmPilot.Plugin.Abstractions;

namespace SlurmPilot.App.Services.Plugins;

public sealed class LoadedPlugin
{
    private readonly ISlurmPilotPlugin _plugin;
    private readonly IPluginContext _context;

    internal LoadedPlugin(ISlurmPilotPlugin plugin, IPluginContext context,
        PluginLoadContext loadContext, string assemblyPath)
    {
        _plugin = plugin;
        _context = context;
        LoadContext = loadContext;
        AssemblyPath = assemblyPath;
    }

    public string Id => _plugin.Id;
    public string DisplayName => _plugin.DisplayName;
    public string Description => _plugin.Description;
    public string Icon => _plugin.Icon;
    public int Order => _plugin.Order;
    public string AssemblyPath { get; }
    public FrameworkElement? View { get; private set; }
    internal PluginLoadContext LoadContext { get; }

    public FrameworkElement Activate()
        => View ??= _plugin.CreateView(_context)
                   ?? throw new InvalidOperationException("CreateView returned null.");

    internal void Unload(IAppLogger? logger)
    {
        try { _plugin.OnUnloading(); }
        catch (Exception ex) { logger?.Error($"Plugin '{Id}' failed during OnUnloading.", ex); }

        if (_plugin is IDisposable disposable)
        {
            try { disposable.Dispose(); }
            catch (Exception ex) { logger?.Error($"Plugin '{Id}' failed during Dispose.", ex); }
        }

        View = null;
    }
}

public sealed class PluginManager : IDisposable
{
    private readonly IAppLogger? _logger;
    private readonly Action<string, string, string?> _showInformation;
    private readonly Action<string, string, string?> _showWarning;
    private readonly Dictionary<string, LoadedPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);

    public PluginManager(
        IAppLogger? logger = null,
        Action<string, string, string?>? showInformation = null,
        Action<string, string, string?>? showWarning = null)
    {
        _logger = logger;
        _showInformation = showInformation ?? ((title, message, details) =>
            AppDialogService.ShowInfo(title, message, details));
        _showWarning = showWarning ?? ((title, message, details) =>
            AppDialogService.ShowWarning(title, message, details));
        PluginDirectory = Path.Combine(AppContext.BaseDirectory, "plugin");
    }

    public string PluginDirectory { get; }
    public IReadOnlyList<string> LoadErrors { get; private set; } = [];

    public IReadOnlyList<LoadedPlugin> Reload()
    {
        UnloadAll();
        Directory.CreateDirectory(PluginDirectory);
        var errors = new List<string>();
        foreach (var assemblyPath in Directory.EnumerateFiles(PluginDirectory, "*Plugin.dll", SearchOption.TopDirectoryOnly))
            LoadAssembly(assemblyPath, errors);
        LoadErrors = errors;
        return Snapshot();
    }

    public bool Unload(string pluginId)
    {
        if (!_plugins.Remove(pluginId, out var plugin)) return false;
        plugin.Unload(_logger);
        plugin.LoadContext.Unload();
        _logger?.Info($"Unloaded plugin '{pluginId}'.");
        return true;
    }

    public void Dispose() => UnloadAll();

    private void LoadAssembly(string assemblyPath, List<string> errors)
    {
        PluginLoadContext? loadContext = null;
        var contextRetained = false;
        try
        {
            loadContext = new PluginLoadContext(assemblyPath);
            var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
            var types = GetLoadableTypes(assembly)
                .Where(type => !type.IsAbstract && !type.IsInterface
                               && typeof(ISlurmPilotPlugin).IsAssignableFrom(type)
                               && type.GetConstructor(Type.EmptyTypes) != null)
                .ToArray();

            if (types.Length > 1)
                throw new InvalidOperationException("A plugin entry assembly must contain exactly one ISlurmPilotPlugin implementation.");

            foreach (var type in types)
            {
                try
                {
                    var plugin = (ISlurmPilotPlugin)Activator.CreateInstance(type)!;
                    ValidatePlugin(plugin);
                    if (_plugins.ContainsKey(plugin.Id))
                        throw new InvalidOperationException($"Duplicate plugin id '{plugin.Id}'.");
                    var folder = Path.GetDirectoryName(assemblyPath) ?? PluginDirectory;
                    var context = new PluginContext(folder, _logger, plugin.Id,
                        _showInformation, _showWarning);
                    _plugins.Add(plugin.Id, new LoadedPlugin(plugin, context, loadContext, assemblyPath));
                    contextRetained = true;
                    _logger?.Info($"Discovered plugin '{plugin.Id}' from '{assemblyPath}'. UI creation is deferred.");
                }
                catch (Exception ex)
                {
                    var message = $"{Path.GetFileName(assemblyPath)} / {type.FullName}: {ex.Message}";
                    errors.Add(message);
                    _logger?.Error($"Failed to initialize plugin. {message}", ex);
                }
            }
        }
        catch (BadImageFormatException) { }
        catch (Exception ex)
        {
            var message = $"{Path.GetFileName(assemblyPath)}: {ex.Message}";
            errors.Add(message);
            _logger?.Error($"Failed to inspect plugin assembly. {message}", ex);
        }
        finally
        {
            if (!contextRetained) loadContext?.Unload();
        }
    }

    private IReadOnlyList<LoadedPlugin> Snapshot()
        => _plugins.Values.OrderBy(p => p.Order)
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();

    private void UnloadAll()
    {
        foreach (var plugin in _plugins.Values)
        {
            plugin.Unload(_logger);
            plugin.LoadContext.Unload();
        }
        _plugins.Clear();
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
    }

    private static void ValidatePlugin(ISlurmPilotPlugin plugin)
    {
        if (string.IsNullOrWhiteSpace(plugin.Id)) throw new InvalidOperationException("Plugin Id cannot be empty.");
        if (string.IsNullOrWhiteSpace(plugin.DisplayName)) throw new InvalidOperationException($"Plugin '{plugin.Id}' has no display name.");
    }

    private sealed class PluginContext(
        string pluginDirectory,
        IAppLogger? logger,
        string pluginId,
        Action<string, string, string?> showInformation,
        Action<string, string, string?> showWarning) : IPluginContext
    {
        public string PluginDirectory { get; } = pluginDirectory;
        public Version HostVersion { get; } = typeof(App).Assembly.GetName().Version ?? new Version(0, 0);

        public void Log(PluginLogLevel level, string message, Exception? exception = null)
        {
            var formatted = $"[Plugin:{pluginId}] {message}";
            switch (level)
            {
                case PluginLogLevel.Debug: logger?.Debug(formatted); break;
                case PluginLogLevel.Information: logger?.Info(formatted); break;
                case PluginLogLevel.Warning: logger?.Warning(formatted); break;
                case PluginLogLevel.Error: logger?.Error(formatted, exception); break;
            }
        }

        public void ShowInformation(string title, string message, string? details = null)
            => InvokeOnUi(() => showInformation(title, message, details));

        public void ShowWarning(string title, string message, string? details = null)
            => InvokeOnUi(() => showWarning(title, message, details));

        private static void InvokeOnUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }
    }
}
