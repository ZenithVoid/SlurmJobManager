using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using SlurmPilot.Plugin.Abstractions;

namespace SlurmPilot.App.Services.Plugins;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string ContractAssemblyName =
        typeof(ISlurmPilotPlugin).Assembly.GetName().Name!;

    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginDirectory;

    public PluginLoadContext(string pluginAssemblyPath)
        : base($"SlurmPilot.Plugin:{Path.GetFileNameWithoutExtension(pluginAssemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
        _pluginDirectory = Path.GetDirectoryName(pluginAssemblyPath)!;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // The contract must come from the default context, otherwise interface identity differs.
        if (string.Equals(assemblyName.Name, ContractAssemblyName, StringComparison.OrdinalIgnoreCase))
            return null;

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath is not null)
            return LoadFromAssemblyPath(assemblyPath);

        var adjacentPath = Path.Combine(_pluginDirectory, $"{assemblyName.Name}.dll");
        return File.Exists(adjacentPath) ? LoadFromAssemblyPath(adjacentPath) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(libraryPath);
    }
}
