using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MADTOM.PluginContracts;

namespace MADTOM.Console.Hosting;

public sealed class LoadedPluginDescriptor
{
    public required IPluginModule Module { get; init; }
    public PluginLoadContext? LoadContext { get; init; }
    public WeakReference? AlcWeakRef { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>
/// Discovers, loads, initializes, and tears down MADTOM plugins.
/// Follows the in-process teardown sequence detailed in the Obsidian reference architecture.
/// </summary>
public sealed class PluginManager : IAsyncDisposable
{
    private readonly ConsoleHostContext _hostContext;
    private readonly List<LoadedPluginDescriptor> _plugins = new();

    public IReadOnlyList<LoadedPluginDescriptor> LoadedPlugins => _plugins;

    public PluginManager(ConsoleHostContext hostContext)
    {
        _hostContext = hostContext;
    }

    /// <summary>
    /// Registers and initializes a pre-instantiated plugin module.
    /// </summary>
    public async Task RegisterModuleAsync(IPluginModule module, CancellationToken ct = default)
    {
        await module.InitializeAsync(_hostContext, ct);
        await module.StartAsync(ct);

        _plugins.Add(new LoadedPluginDescriptor
        {
            Module = module,
            IsActive = true
        });
    }

    /// <summary>
    /// Discovers and loads external plugin DLLs from a given directory using collectible ALCs.
    /// </summary>
    public async Task LoadPluginsFromDirectoryAsync(string directoryPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(directoryPath))
            return;

        var dllFiles = Directory.GetFiles(directoryPath, "*.dll", SearchOption.AllDirectories);

        foreach (var dllPath in dllFiles)
        {
            try
            {
                var alc = new PluginLoadContext(dllPath);
                var asm = alc.LoadFromAssemblyPath(Path.GetFullPath(dllPath));

                var pluginTypes = asm.GetTypes()
                    .Where(t => typeof(IPluginModule).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

                foreach (var type in pluginTypes)
                {
                    if (Activator.CreateInstance(type) is IPluginModule module)
                    {
                        await module.InitializeAsync(_hostContext, ct);
                        await module.StartAsync(ct);

                        _plugins.Add(new LoadedPluginDescriptor
                        {
                            Module = module,
                            LoadContext = alc,
                            AlcWeakRef = new WeakReference(alc),
                            IsActive = true
                        });
                    }
                }
            }
            catch (Exception)
            {
                // Continue scanning remaining DLLs
            }
        }
    }

    /// <summary>
    /// Gracefully stops and unloads a specific plugin.
    /// </summary>
    public async Task UnloadPluginAsync(LoadedPluginDescriptor descriptor)
    {
        descriptor.IsActive = false;

        // 1. Cooperative cancellation and stop
        await descriptor.Module.StopAsync();

        // 2. Dispose module
        await descriptor.Module.DisposeAsync();

        // 3. If loaded via custom ALC, trigger unload
        if (descriptor.LoadContext != null)
        {
            descriptor.LoadContext.Unload();
        }

        _plugins.Remove(descriptor);

        // Force GC sweep to collect collectible ALC
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _plugins.ToList())
        {
            try
            {
                await p.Module.StopAsync();
                await p.Module.DisposeAsync();
                p.LoadContext?.Unload();
            }
            catch { }
        }

        _plugins.Clear();
        GC.Collect();
    }
}

