using CommunityToolkit.Mvvm.Messaging;

namespace MADTOM.PluginContracts;

/// <summary>
/// Context passed by MADTOM Console to plugins upon initialization.
/// Provides access to host-level services without tight coupling.
/// </summary>
public interface IPluginHostContext
{
    /// <summary>
    /// Host toast and notification service.
    /// </summary>
    IPluginNotificationService Notifications { get; }

    /// <summary>
    /// Host lexicon and localization service.
    /// </summary>
    ILexiconHost Lexicons { get; }

    /// <summary>
    /// Host theme manager.
    /// </summary>
    IThemeHost Themes { get; }

    /// <summary>
    /// Weak event messenger for decoupling host-to-plugin and plugin-to-plugin communication.
    /// Prevents strong delegate references that would anchor plugins and prevent GC unloading.
    /// </summary>
    IMessenger Messenger { get; }

    /// <summary>
    /// Host system tray menu service allowing plugins to register dynamic status sections.
    /// Returns null if running in standalone mode or if the host does not provide system tray integration.
    /// </summary>
    ITrayMenuService? Tray { get; }
}

