using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace MADTOM.PluginContracts;

/// <summary>
/// Primary entry point for any MADTOM plugin module.
/// Encapsulates metadata, lifecycle, and in-process visual creation.
/// </summary>
public interface IPluginModule : IPluginLifecycle
{
    /// <summary>
    /// Unique identifier for the plugin (e.g. "telemetry", "sysadmin", "esc", "iot").
    /// </summary>
    string Id { get; }

    /// <summary>
    /// User-visible name of the module.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Brief description of what the module does.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Icon glyph or vector key used for the sidebar tab representation.
    /// </summary>
    string IconGlyph { get; }

    /// <summary>
    /// Grouping category (e.g. "Operations", "Systems", "Hardware", "Network").
    /// </summary>
    string Category { get; }

    /// <summary>
    /// Plugin version.
    /// </summary>
    Version Version { get; }

    /// <summary>
    /// Preferred order weight in navigation menus (lower values appear first).
    /// </summary>
    int OrderWeight { get; }

    /// <summary>
    /// Called by the host to supply the host context and let the plugin register
    /// internal DI services, lexicons, and custom styles.
    /// </summary>
    Task InitializeAsync(IPluginHostContext hostContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or returns the root visual Control (typically a UserControl) to mount into MADTOM Console.
    /// </summary>
    Control CreateView();
}

