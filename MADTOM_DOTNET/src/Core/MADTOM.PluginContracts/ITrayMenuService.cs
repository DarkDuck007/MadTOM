using System;
using System.Collections.Generic;

namespace MADTOM.PluginContracts;

/// <summary>
/// Descriptor representing an individual item, label, action, or separator in the system tray menu.
/// Pure C# model completely decoupled from any UI framework.
/// </summary>
public sealed class TrayMenuItemDescriptor
{
    public string Text { get; set; } = string.Empty;
    public string? IconSymbol { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsVisible { get; set; } = true;
    public bool IsSeparator { get; set; } = false;
    public Action? Action { get; set; }
    public IReadOnlyList<TrayMenuItemDescriptor>? SubItems { get; set; }

    public static TrayMenuItemDescriptor Separator() => new() { IsSeparator = true };

    public static TrayMenuItemDescriptor TextItem(string text, bool enabled = false) =>
        new() { Text = text, IsEnabled = enabled };

    public static TrayMenuItemDescriptor ActionItem(string text, Action action) =>
        new() { Text = text, Action = action, IsEnabled = true };
}

/// <summary>
/// A logical section contributed by a plugin to the host's system tray menu.
/// </summary>
public interface ITrayMenuSection
{
    /// <summary>
    /// Unique identifier for this section.
    /// </summary>
    string SectionId { get; }

    /// <summary>
    /// Display name of the plugin or component owning this section (e.g. "MADTOM Telemetry").
    /// </summary>
    string PluginName => SectionId;

    /// <summary>
    /// Ordering weight in the tray menu (lower values appear higher).
    /// </summary>
    int OrderWeight { get; }

    /// <summary>
    /// Current list of menu item descriptors for this section.
    /// </summary>
    IReadOnlyList<TrayMenuItemDescriptor> GetItems();

    /// <summary>
    /// Event raised when the section's items change dynamically.
    /// </summary>
    event EventHandler? ItemsChanged;
}

/// <summary>
/// Host service provided by MADTOM Console allowing plugins to register dynamic tray menu sections.
/// </summary>
public interface ITrayMenuService
{
    /// <summary>
    /// Registers a tray menu section contributed by a plugin.
    /// Returns an IDisposable token to unregister the section on disposal.
    /// </summary>
    IDisposable RegisterSection(ITrayMenuSection section);

    /// <summary>
    /// Event raised when sections are added, removed, or update their items.
    /// </summary>
    event EventHandler? SectionsChanged;

    /// <summary>
    /// Gets all registered sections ordered by OrderWeight.
    /// </summary>
    IReadOnlyList<ITrayMenuSection> GetSections();
}

