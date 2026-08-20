using System.Windows;

namespace SlurmPilot.Plugin.Abstractions;

/// <summary>
/// Contract implemented by SlurmPilot UI plugins. Implementations must expose a public,
/// parameterless constructor so the host can discover them at runtime.
/// </summary>
public interface ISlurmPilotPlugin
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    string Icon { get; }
    int Order => 0;

    FrameworkElement CreateView(IPluginContext context);

    /// <summary>Called before the host detaches and unloads the plugin.</summary>
    void OnUnloading() { }
}

/// <summary>Restricted host services made available to a plugin.</summary>
public interface IPluginContext
{
    string PluginDirectory { get; }
    Version HostVersion { get; }
    void Log(PluginLogLevel level, string message, Exception? exception = null);
    void ShowInformation(string title, string message, string? details = null);
    void ShowWarning(string title, string message, string? details = null);
}

public enum PluginLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
}
