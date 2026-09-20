using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace MADTOM.Console.Hosting;

/// <summary>
/// Collectible AssemblyLoadContext that isolates plugin dependencies while sharing
/// MADTOM.PluginContracts and Avalonia core primitives with the Host default context.
/// Reference: "Plugins in Avalonia UI" - Section Dynamic Assembly Loading.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 1. Never isolate shared contracts or Avalonia UI runtime - delegate to Host ALC
        if (assemblyName.Name != null)
        {
            if (assemblyName.Name.StartsWith("MADTOM.PluginContracts", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.Name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.Name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.Name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.Name.Equals("CommunityToolkit.Mvvm", StringComparison.OrdinalIgnoreCase))
            {
                return null; // Fallback to Default ALC
            }
        }

        // 2. Resolve plugin-local assembly
        string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath != null && File.Exists(assemblyPath))
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath != null && File.Exists(libraryPath))
        {
            return LoadUnmanagedDllFromPath(libraryPath);
        }

        return IntPtr.Zero;
    }
}

